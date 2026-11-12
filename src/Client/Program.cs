using Microsoft.Win32;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

[assembly: ComVisible(true)]
namespace RdpProxy.Client;

internal static class Program
{
    private const string AddIn = @"Software\Microsoft\Terminal Server Client\Default\AddIns\RdpProxy";
    private static string ComKey => $@"Software\Classes\CLSID\{RdpPlugin.PluginClsid:B}";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows x64 required.");
            Diagnostics.Initialize("Client");
            if (args.Contains("--help"))
            {
                TrayHost.ShowInformation("Client", "RdpProxy.Client.exe [/register | /unregister]\n" +
                    "RdpProxy.Client.exe [--mode server-proxy|client-proxy] [--port PORT] [--auth-file PATH]\n" +
                    "Default: server-proxy. client-proxy listens on 127.0.0.1, default 1080.\n" +
                    "Start both endpoints with the same mode. Registration uses HKCU only.");
                return 0;
            }
            if (args.Contains("/register", StringComparer.OrdinalIgnoreCase))
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var addin = root.CreateSubKey(AddIn);
                    addin.SetValue("Name", RdpPlugin.PluginClsid.ToString("B"));
                    using var com = root.CreateSubKey(ComKey + @"\LocalServer32");
                    com.SetValue(null, "\"" + Environment.ProcessPath + "\"");
                }
                Diagnostics.Write("client-registration-complete views=x64,x86");
                return 0;
            }
            if (args.Contains("/unregister", StringComparer.OrdinalIgnoreCase))
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    root.DeleteSubKeyTree(AddIn, false);
                    root.DeleteSubKeyTree(ComKey, false);
                }
                Diagnostics.Write("client-unregistration-complete views=x64,x86");
                return 0;
            }
            ProxyOptions options = ProxyOptions.Parse(args, true);
            using var mutex = new Mutex(true, @"Local\" + StatusHost.PipeName("Client"), out bool owner);
            if (!owner) throw new IOException("Client already running in this desktop session. Stop it before changing mode.");
            try
            {
                Diagnostics.Write($"mode-selected side=Client mode={options.Mode} listener={options.HasListener}");
                RdpPlugin.Mode = options.Mode;
                return TrayHost.Run("Client", options, (ct, started) => RunServiceAsync(options, ct, started));
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Diagnostics.Fault("client-main", ex);
            TrayHost.ShowError("Client", ex.Message);
            return 1;
        }
        finally { Diagnostics.Stop(); }
    }

    private static Task RunServiceAsync(ProxyOptions options, CancellationToken ct, Action started)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunService(options, ct, started);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "RdpProxy client COM server" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    private static void RunService(ProxyOptions options, CancellationToken ct, Action started)
    {
        // This method must stay synchronous: every LocalServer call, including
        // CoUninitialize after the asynchronous drain, belongs to this MTA thread.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LocalServer? server = null;
        LocalProxy? proxy = null;
        Task socks = Task.CompletedTask;
        Task status = Task.CompletedTask;
        Task monitor = Task.CompletedTask;
        var failures = new List<Exception>();
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            server = new LocalServer();
            proxy = options.HasListener ? new LocalProxy(options.Port, "Client", options.Mode, options.AuthFile) : null;
            server.RegisterClass<RdpPlugin>(RdpPlugin.PluginClsid);
            socks = proxy?.RunAsync(LocalProxy.SelectClientSession, stop.Token) ?? Task.CompletedTask;
            status = StatusHost.RunAsync("Client", proxy?.Port, stop.Token, options.Mode, proxy?.Credentials.Path);
            monitor = MonitorServicesAsync(stop, proxy == null ? new[] { status } : new[] { socks, status });
            stop.Token.ThrowIfCancellationRequested();
            started();
            server.Run(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            RdpPlugin.StopAccepting();
            Cleanup(() => server?.StopActivations());
            Cleanup(stop.Cancel);
            Cleanup(() => Task.WhenAll(RdpPlugin.StopAllAsync(), socks, status, monitor).GetAwaiter().GetResult());
            Cleanup(() => Session.DrainConnectionsAsync().GetAwaiter().GetResult());
            Cleanup(() => proxy?.Dispose());
            Cleanup(() => server?.Dispose());
        }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Client service failed during startup or shutdown.", failures);

        void Cleanup(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { failures.Add(ex); }
        }
    }

    private static async Task MonitorServicesAsync(CancellationTokenSource stop, Task[] services)
    {
        Task completed = await Task.WhenAny(services).ConfigureAwait(false);
        if (stop.IsCancellationRequested) return;
        try
        {
            await completed.ConfigureAwait(false);
            throw new IOException("A client background service stopped unexpectedly. See the diagnostic log.");
        }
        finally { stop.Cancel(); }
    }
}
