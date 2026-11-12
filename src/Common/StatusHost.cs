using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace RdpProxy;

internal static class StatusHost
{
    public static string PipeName(string side) =>
        $"RdpProxy-{side}-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";

    public static async Task RunAsync(string side, int? port, CancellationToken ct, ProxyMode mode = ProxyMode.ServerProxy, string? credentialFile = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName(side), PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(2000);
                try
                {
                    var sessions = Session.Active.Values.ToArray();
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Side = side, Build = ProxyOptions.Build, Mode = mode.ToString(), CredentialFile = credentialFile, SessionId = Process.GetCurrentProcess().SessionId,
                        ProcessId = Environment.ProcessId, DiagnosticLog = Diagnostics.LogPath, Host = port == null ? null : "127.0.0.1", Port = port,
                        ChannelCount = sessions.Length,
                        ReadyChannelCount = sessions.Count(s => s.Ready),
                        Channels = sessions.Select(s => s.Status()).ToArray()
                    });
                    await pipe.WriteAsync(bytes, deadline.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Status pipe unavailable: {ex.GetType().Name}"); }
    }
}
