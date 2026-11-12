using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpProxy;

internal static class TrayHost
{
    public static int Run(string side, ProxyOptions options, Func<CancellationToken, Action, Task> runService)
    {
        if (!IsInteractive())
        {
            ShowError(side, "Для запуска RdpProxy нужна интерактивная пользовательская сессия Windows.");
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        using var context = new TrayContext(side, options, runService);
        ThreadExceptionEventHandler onThreadException = (_, args) => context.ReportUiFailure(args.Exception);
        Application.ThreadException += onThreadException;
        try { Application.Run(context); }
        catch (Exception error) { context.ReportUiFailure(error); }
        finally
        {
            Application.ThreadException -= onThreadException;
            // WinForms may already have disposed ApplicationContext and its windows.
            // The background service owns no UI work, so drain its actual tasks here.
            context.DrainAfterMessageLoop();
        }
        return context.ExitCode;
    }

    public static void ShowError(string side, string message) => ShowMessage(side, message, MessageBoxIcon.Error);

    public static void ShowInformation(string side, string message) => ShowMessage(side, message, MessageBoxIcon.Information);

    private static string Title(string side) => "RdpProxy — " +
        (side.Equals("client", StringComparison.OrdinalIgnoreCase) ? "клиент" : "сервер");

    private static bool IsInteractive()
    {
        if (!Environment.UserInteractive) return false;
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId != 0;
        }
        catch { return false; }
    }

    private static void ShowMessage(string side, string message, MessageBoxIcon icon)
    {
        if (!IsInteractive())
        {
            if (icon == MessageBoxIcon.Error) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
            return;
        }
        MessageBox.Show(message, Title(side), MessageBoxButtons.OK, icon);
    }

    private enum TrayState { Starting, Waiting, Connected, Stopping, Error }

    private sealed record ChannelStatus(string Id, string Owner, bool Ready, int Streams, long Sent, long Received);
    private sealed record Snapshot(ChannelStatus[] Channels)
    {
        public int ReadyCount => Channels.Count(channel => channel.Ready);
        public int StreamCount => Channels.Sum(channel => channel.Streams);
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly string side;
        private readonly ProxyOptions options;
        private readonly int sessionId;
        private readonly Control dispatcher = new();
        private readonly CancellationTokenSource stop = new();
        private readonly object lifetimeGate = new();
        private readonly ContextMenuStrip menu = new();
        private readonly NotifyIcon tray = new();
        private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
        private readonly Dictionary<TrayState, Icon> icons = new();
        private readonly ToolStripMenuItem stateItem = new() { Enabled = false };
        private readonly ToolStripMenuItem exitItem = new("Выход");
        private StatusWindow? statusWindow;
        private Snapshot snapshot = new(Array.Empty<ChannelStatus>());
        private Task serviceTask = Task.CompletedTask;
        private Task serviceObserver = Task.CompletedTask;
        private Task? cancellationTask;
        private Task cancellationObserver = Task.CompletedTask;
        private Exception? failure;
        private bool started;
        private bool serviceFinished;
        private bool cancellationFinished;
        private bool statusUnavailable;
        private bool subscribedToSessionEnding;
        private int stopRequested;
        private int sessionEnding;
        private int refreshPending;
        private int disposed;
        private int loopEnded;
        private int lifetimeDrained;
        private int exitCode;
        private bool errorShown;

        public bool Completed { get; private set; }
        public int ExitCode => Volatile.Read(ref exitCode);

        public TrayContext(string side, ProxyOptions options, Func<CancellationToken, Action, Task> runService)
        {
            this.side = side;
            this.options = options;
            using var process = Process.GetCurrentProcess();
            sessionId = process.SessionId;
            try
            {
                // BeginInvoke must have a stable handle even before Application.Run starts.
                _ = dispatcher.Handle;
                foreach (TrayState state in Enum.GetValues<TrayState>()) icons.Add(state, CreateIcon(options.IsClient, state));
                var statusItem = new ToolStripMenuItem("Статус…");
                statusItem.Click += (_, _) => ShowStatus();
                var logItem = new ToolStripMenuItem("Открыть журнал");
                logItem.Click += (_, _) => OpenLog();
                exitItem.Click += (_, _) => RequestStop();
                menu.Items.AddRange(new ToolStripItem[] { stateItem, new ToolStripSeparator(), statusItem, logItem, new ToolStripSeparator(), exitItem });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += (_, _) => ShowStatus();
                UpdateDisplay();
                tray.Visible = true;
                refreshTimer.Tick += RefreshStatus;
                refreshTimer.Start();
                SystemEvents.SessionEnding += OnSessionEnding;
                subscribedToSessionEnding = true;

                // The delegate may synchronously enter COM/native setup before returning a Task.
                // Never execute that setup (or CancellationToken callbacks) on the UI thread.
                serviceTask = Task.Run(() => runService(stop.Token, () => Post(OnStarted)));
                serviceObserver = ObserveServiceAsync(serviceTask);
            }
            catch
            {
                try { Dispose(); }
                finally { DrainAfterMessageLoop(reportErrors: false); }
                throw;
            }
        }

        private void Post(Action action)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref loopEnded) != 0) return;
            try
            {
                dispatcher.BeginInvoke((Action)(() =>
                {
                    if (!Completed && Volatile.Read(ref disposed) == 0) action();
                }));
            }
            catch (InvalidOperationException error)
            {
                // Windows may already be tearing down the desktop during logoff.
                Diagnostics.Fault("tray-dispatch", error);
            }
        }

        private async Task ObserveServiceAsync(Task service)
        {
            Exception? error = null;
            try { await service.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception caught) { error = caught; }
            if (error != null) RecordFailure(error);
            if (Volatile.Read(ref stopRequested) == 0)
            {
                if (error == null) RecordFailure(new InvalidOperationException("Фоновая служба неожиданно завершилась."));
                RequestStop();
            }
            Post(OnServiceFinished);
        }

        private void OnStarted()
        {
            if (serviceFinished) return;
            started = true;
            UpdateDisplay();
        }

        public void RequestStop()
        {
            lock (lifetimeGate)
            {
                if (cancellationTask != null) return;
                Volatile.Write(ref stopRequested, 1);
                Diagnostics.Write("tray-stop-requested");
                cancellationTask = Task.Run(() => stop.Cancel());
                cancellationObserver = ObserveCancellationAsync(cancellationTask);
            }
            Post(UpdateDisplay);
        }

        private async Task ObserveCancellationAsync(Task cancellation)
        {
            Exception? error = null;
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception caught) { error = caught; }
            if (error != null) RecordFailure(error);
            Post(() =>
            {
                cancellationFinished = true;
                FinishIfDrained();
            });
        }

        private void OnServiceFinished()
        {
            serviceFinished = true;
            UpdateDisplay();
            FinishIfDrained();
        }

        public void ReportUiFailure(Exception error)
        {
            RecordFailure(error);
            RequestStop();
            if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref loopEnded) == 0) FinishIfDrained();
        }

        private void RecordFailure(Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            Interlocked.Exchange(ref exitCode, 1);
            Diagnostics.Fault("tray-service", error);
        }

        public void DrainAfterMessageLoop(bool reportErrors = true)
        {
            if (Interlocked.Exchange(ref lifetimeDrained, 1) != 0) return;
            Volatile.Write(ref loopEnded, 1);
            if (!Completed && reportErrors && Volatile.Read(ref sessionEnding) == 0)
                RecordFailure(new InvalidOperationException("Цикл обработки интерфейса неожиданно завершился."));
            RequestStop();
            Task cancellation;
            Task cancellationCompletion;
            lock (lifetimeGate)
            {
                cancellation = cancellationTask!;
                cancellationCompletion = cancellationObserver;
            }
            try
            {
                // This fallback runs only after the message pump has gone away. Raw
                // task completion, not BeginInvoke delivery, proves the service drained.
                // Observers retain/log faults even when the dispatcher is disposed.
                try { serviceTask.GetAwaiter().GetResult(); } catch { }
                try { cancellation.GetAwaiter().GetResult(); } catch { }
                try { serviceObserver.GetAwaiter().GetResult(); }
                catch (Exception error) { RecordFailure(error); }
                try { cancellationCompletion.GetAwaiter().GetResult(); }
                catch (Exception error) { RecordFailure(error); }
            }
            finally
            {
                stop.Dispose();
                Completed = true;
            }
            if (reportErrors) ShowFailureOnce();
        }

        private void ShowFailureOnce()
        {
            Exception? error = Volatile.Read(ref failure);
            if (error == null || errorShown || Volatile.Read(ref sessionEnding) != 0) return;
            errorShown = true;
            try
            {
                string log = Diagnostics.LogPath is { } path ? "\n\nЖурнал: " + path : "";
                ShowError(side, "RdpProxy завершает работу из-за ошибки.\n\n" + error.Message + log);
            }
            catch (Exception displayError) { RecordFailure(displayError); }
        }

        private void FinishIfDrained()
        {
            if (!serviceFinished || !cancellationFinished || Completed) return;
            try
            {
                refreshTimer.Stop();
                UpdateDisplay();
                ShowFailureOnce();
            }
            catch (Exception error) { RecordFailure(error); }
            finally
            {
                Completed = true;
                try
                {
                    tray.Visible = false;
                    statusWindow?.Close();
                }
                finally { ExitThread(); }
            }
        }

        private void OnSessionEnding(object? sender, SessionEndingEventArgs args)
        {
            // Best effort only: never veto Windows logoff or wait in its notification.
            Interlocked.Exchange(ref sessionEnding, 1);
            RequestStop();
        }

        private void RefreshStatus(object? sender, EventArgs args)
        {
            if (Interlocked.CompareExchange(ref refreshPending, 1, 0) != 0) return;
            _ = CaptureStatusAsync();
        }

        private async Task CaptureStatusAsync()
        {
            try
            {
                Snapshot next = await Task.Run(() =>
                {
                    var channels = Session.Active.Values.Select(session =>
                    {
                        JsonElement value = JsonSerializer.SerializeToElement(session.Status());
                        return new ChannelStatus(
                            value.GetProperty("ChannelId").GetString() ?? "",
                            value.GetProperty("OwnerId").GetString() ?? "",
                            value.GetProperty("Connected").GetBoolean(),
                            value.GetProperty("Streams").GetInt32(),
                            value.GetProperty("SentBytes").GetInt64(),
                            value.GetProperty("ReceivedBytes").GetInt64());
                    }).OrderBy(channel => channel.Id, StringComparer.Ordinal).ToArray();
                    return new Snapshot(channels);
                }).ConfigureAwait(false);
                Post(() =>
                {
                    snapshot = next;
                    statusUnavailable = false;
                    UpdateDisplay();
                });
            }
            catch (Exception error)
            {
                Diagnostics.Fault("tray-status", error);
                Post(() => { statusUnavailable = true; UpdateDisplay(); });
            }
            finally { Volatile.Write(ref refreshPending, 0); }
        }

        private TrayState CurrentState => failure != null ? TrayState.Error :
            Volatile.Read(ref stopRequested) != 0 ? TrayState.Stopping : !started ? TrayState.Starting :
            snapshot.ReadyCount > 0 ? TrayState.Connected : TrayState.Waiting;

        private string StateText => CurrentState switch
        {
            TrayState.Starting => "Запуск…",
            TrayState.Waiting => "Ожидание RDP-канала",
            TrayState.Connected => $"Подключено: {snapshot.ReadyCount}; потоков: {snapshot.StreamCount}",
            TrayState.Stopping => "Завершение работы…",
            _ => "Ошибка — завершение работы"
        };

        private void UpdateDisplay()
        {
            tray.Icon = icons[CurrentState];
            string tooltip = Title(side) + ": " + StateText;
            tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
            stateItem.Text = StateText;
            exitItem.Enabled = Volatile.Read(ref stopRequested) == 0;
            exitItem.Text = exitItem.Enabled ? "Выход" : "Завершение…";
            statusWindow?.UpdateStatus(StateText, snapshot, statusUnavailable, Diagnostics.LogPath);
        }

        private void ShowStatus()
        {
            if (statusWindow == null || statusWindow.IsDisposed)
            {
                statusWindow = new StatusWindow(side, options, sessionId, OpenLog);
                statusWindow.FormClosed += (_, _) => statusWindow = null;
            }
            UpdateDisplay();
            statusWindow.Show();
            if (statusWindow.WindowState == FormWindowState.Minimized) statusWindow.WindowState = FormWindowState.Normal;
            statusWindow.Activate();
        }

        private void OpenLog()
        {
            try
            {
                string? path = Diagnostics.LogPath;
                if (path == null || !File.Exists(path))
                {
                    ShowInformation(side, "Файл журнала пока недоступен. Он появится после первой записи диагностики.");
                    return;
                }
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                Diagnostics.Fault("tray-open-log", error);
                ShowError(side, "Не удалось открыть журнал.\n\n" + error.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                // Application.Run may dispose us after WM_QUIT before the service
                // finishes. Cancellation and task ownership outlive these UI resources.
                RequestStop();
                if (subscribedToSessionEnding) SystemEvents.SessionEnding -= OnSessionEnding;
                refreshTimer.Stop();
                refreshTimer.Tick -= RefreshStatus;
                refreshTimer.Dispose();
                statusWindow?.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                foreach (Icon icon in icons.Values) icon.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StatusWindow : Form
    {
        private readonly ProxyOptions options;
        private readonly int sessionId;
        private readonly TextBox summary = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, TabStop = false };
        private readonly TextBox logPath = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, TabStop = false };
        private readonly DataGridView channels = new()
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoGenerateColumns = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
        };

        public StatusWindow(string side, ProxyOptions options, int sessionId, Action openLog)
        {
            this.options = options;
            this.sessionId = sessionId;
            Text = Title(side) + " — статус";
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 540);
            MinimumSize = new Size(800, 460);
            Font = SystemFonts.MessageBoxFont;
            summary.BackColor = BackColor;
            logPath.BackColor = BackColor;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            AddColumn("Канал", 180);
            AddColumn("Владелец", 180);
            AddColumn("Состояние", 100);
            AddColumn("Потоки", 60);
            AddColumn("Отправлено, байт", 115);
            AddColumn("Получено, байт", 115);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var hide = new Button { Text = "Скрыть", AutoSize = true };
            hide.Click += (_, _) => Close();
            var log = new Button { Text = "Открыть журнал", AutoSize = true };
            log.Click += (_, _) => openLog();
            buttons.Controls.Add(hide);
            buttons.Controls.Add(log);
            CancelButton = hide;
            layout.Controls.Add(summary, 0, 0);
            layout.Controls.Add(channels, 0, 1);
            layout.Controls.Add(logPath, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            Controls.Add(layout);
        }

        private void AddColumn(string header, float weight) => channels.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header, FillWeight = weight, MinimumWidth = 65, SortMode = DataGridViewColumnSortMode.NotSortable
        });

        public void UpdateStatus(string state, Snapshot snapshot, bool unavailable, string? path)
        {
            string mode = options.Mode == ProxyMode.ClientProxy ? "client-proxy (SOCKS на клиенте)" : "server-proxy (SOCKS на RDP-сервере)";
            string role = options.HasListener ? $"SOCKS5: 127.0.0.1:{options.Port}" : "Роль: исходящие сетевые подключения";
            summary.Text = $"Состояние: {state}\r\nСборка: {ProxyOptions.Build}    Режим: {mode}\r\n" +
                $"Сессия Windows: {sessionId}    PID: {Environment.ProcessId}\r\n{role}\r\n" +
                $"Каналов: {snapshot.Channels.Length}; готово: {snapshot.ReadyCount}; потоков: {snapshot.StreamCount}" +
                (unavailable ? "    Обновление статистики временно недоступно." : "");
            logPath.Text = "Журнал: " + (path ?? "недоступен") + "\r\nЗакрытие этого окна оставляет RdpProxy в трее.";
            while (channels.Rows.Count > snapshot.Channels.Length) channels.Rows.RemoveAt(channels.Rows.Count - 1);
            for (int i = 0; i < snapshot.Channels.Length; i++)
            {
                ChannelStatus channel = snapshot.Channels[i];
                if (i == channels.Rows.Count) channels.Rows.Add();
                channels.Rows[i].SetValues(channel.Id, channel.Owner, channel.Ready ? "Готов" : "Подключение…",
                    channel.Streams, channel.Sent.ToString("N0"), channel.Received.ToString("N0"));
            }
        }
    }

    private static Icon CreateIcon(bool isClient, TrayState state)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(isClient ? Color.RoyalBlue : Color.Teal);
            graphics.FillEllipse(background, 1, 1, 29, 29);
            using var font = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(isClient ? "C" : "S", font, Brushes.White, new RectangleF(0, 0, 30, 29), format);
            Color indicator = state switch
            {
                TrayState.Connected => Color.LimeGreen,
                TrayState.Error => Color.Red,
                TrayState.Stopping => Color.Gray,
                _ => Color.Gold
            };
            using var dot = new SolidBrush(indicator);
            graphics.FillEllipse(Brushes.White, 19, 19, 13, 13);
            graphics.FillEllipse(dot, 21, 21, 9, 9);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
