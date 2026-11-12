using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace RdpProxy;

// Bounded asynchronous diagnostics. Never record destinations, credentials or payloads,
// and never let disk/console I/O stall a DVC receive callback or socket pump.
internal static class Diagnostics
{
    private static readonly Channel<string> entries = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private static Task? worker;
    public static string? LogPath { get; private set; }

    public static void Initialize(string side)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpProxy", "logs");
            var info = Directory.CreateDirectory(directory);
            var sid = WindowsIdentity.GetCurrent().User!;
            var acl = new DirectorySecurity();
            acl.SetOwner(sid);
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
            LogPath = Path.Combine(directory, $"{side}-session-{Process.GetCurrentProcess().SessionId}-pid-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.log");
            string path = LogPath;
            worker = Task.Run(async () =>
            {
                try
                {
                    long size = 0;
                    await foreach (string entry in entries.Reader.ReadAllAsync())
                    {
                        if (size > 2 * 1024 * 1024)
                        {
                            File.Move(path, path + ".1", true);
                            size = 0;
                        }
                        await File.AppendAllTextAsync(path, entry + Environment.NewLine, Encoding.UTF8);
                        size += Encoding.UTF8.GetByteCount(entry) + 2;
                    }
                }
                catch { /* Diagnostics must never terminate the transport. */ }
            });
            Console.WriteLine($"Diagnostics v3: {LogPath}");
            Write($"startup side={side} build={ProxyOptions.Build} pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId} transport=io-v2 heartbeat=15000ms");
        }
        catch { Console.Error.WriteLine("Diagnostic log unavailable; transport behavior unchanged."); }
    }

    public static void Write(string text) => entries.Writer.TryWrite(
        $"{DateTime.UtcNow:O} mono={Environment.TickCount64} thread={Environment.CurrentManagedThreadId} {text}");

    public static void Fault(string context, Exception error)
    {
        var types = new List<string>();
        for (Exception? item = error; item != null; item = item.InnerException)
        {
            string code = item switch
            {
                SocketException socket => $" socket={socket.SocketErrorCode} native={socket.NativeErrorCode}",
                Win32Exception win32 => $" native={win32.NativeErrorCode}",
                SocksException socks => $" rep={socks.Reply}",
                _ => ""
            };
            types.Add($"{item.GetType().Name} hr=0x{item.HResult:X8}{code}");
        }
        Write(context + " errors=" + string.Join(" -> ", types));
    }

    public static void Stop()
    {
        entries.Writer.TryComplete();
        try { worker?.Wait(2000); } catch { }
    }
}
