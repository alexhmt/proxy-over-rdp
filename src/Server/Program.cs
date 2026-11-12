using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace RdpProxy.Server;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows x64 required.");
            if (args.Contains("--help"))
            {
                TrayHost.ShowInformation("Server", "RdpProxy.Server.exe [--mode server-proxy|client-proxy] [--port PORT] [--auth-file PATH]\n" +
                    "Default: server-proxy listens on 127.0.0.1, port 1080.\n" +
                    "client-proxy opens destination TCP connections from this server; SOCKS runs on the client.\n" +
                    "Run inside your interactive RDP session, with the same mode as the client.");
                return 0;
            }
            ProxyOptions options = ProxyOptions.Parse(args, false);
            uint sessionId = (uint)Process.GetCurrentProcess().SessionId;
            if (sessionId == 0) throw new IOException("Run inside your interactive RDP session, not Session 0.");
            using var mutex = new Mutex(true, @"Local\" + StatusHost.PipeName("Server"), out bool owner);
            if (!owner) throw new IOException("Server already running in this session. Stop it before changing mode.");
            try
            {
                Diagnostics.Initialize("Server");
                Diagnostics.Write($"mode-selected side=Server mode={options.Mode} listener={options.HasListener}");
                return TrayHost.Run("Server", options,
                    (stop, started) => RunServiceAsync(options, sessionId, stop, started));
            }
            // The entry point stays synchronous: acquire and release on the same STA thread.
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Diagnostics.Fault("server-main", ex);
            TrayHost.ShowError("Server", ex.Message);
            return 1;
        }
        finally { Diagnostics.Stop(); }
    }

    private static async Task RunServiceAsync(ProxyOptions options, uint sessionId,
        CancellationToken requestedStop, Action started)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestedStop);
        var services = new List<(string Name, Task Task)>();
        LocalProxy? proxy = null;
        Exception? failure = null;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            proxy = options.HasListener ? new LocalProxy(options.Port, "Server", options.Mode, options.AuthFile) : null;
            var transport = new DvcTransport(options.Mode);
            var transportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task dvc = Task.Factory.StartNew(() =>
            {
                transportStarted.SetResult();
                transport.Run(sessionId, stop.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            services.Add(("DVC transport", dvc));
            services.Add(("Status pipe", StatusHost.RunAsync("Server", proxy?.Port, stop.Token,
                options.Mode, proxy?.Credentials.Path)));
            if (proxy != null)
                services.Add(("SOCKS listener", proxy.RunAsync(() => transport.Current, stop.Token)));

            await transportStarted.Task;
            stop.Token.ThrowIfCancellationRequested();
            if (services.All(service => !service.Task.IsCompleted)) started();
            Task completed = await Task.WhenAny(services.Select(service => service.Task));
            await completed;
            if (!stop.IsCancellationRequested)
                throw new IOException(services.First(service => service.Task == completed).Name + " stopped unexpectedly.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // Cancellation callbacks must not prevent observation/draining of other workers.
            try { stop.Cancel(); }
            catch (Exception ex) { failure ??= ex; Diagnostics.Fault("server-stop", ex); }
            foreach (var service in services)
            {
                try { await service.Task; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    failure ??= ex;
                    Diagnostics.Fault("server-cleanup " + service.Name, ex);
                }
            }
            try { await Session.DrainConnectionsAsync(); }
            catch (Exception ex) { failure ??= ex; Diagnostics.Fault("server-connections-drain", ex); }
            // DVC.Run owns its session and drains pending native I/O before returning.
            // Keep stop's WaitHandle alive until that thread and all socket handlers finish.
            try { proxy?.Dispose(); }
            catch (Exception ex) { failure ??= ex; Diagnostics.Fault("server-listener-dispose", ex); }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
