using System.Net;
using System.Net.Sockets;

namespace RdpProxy;

// Shared listener lifecycle. Both modes use the same SOCKS parser and credentials.
internal sealed class LocalProxy : IDisposable
{
    private readonly TcpListener listener;
    public Credentials Credentials { get; }
    public int Port { get; }

    public LocalProxy(int port, string side, ProxyMode mode, string? authFile = null)
    {
        Port = port;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(128);
        try { Credentials = new Credentials(port, side, mode, authFile); }
        catch { listener.Stop(); throw; }
        Console.WriteLine($"Mode: {mode}; SOCKS5: 127.0.0.1:{port}; credentials: {Credentials.Path}; Ctrl+C to stop.");
    }

    public Task RunAsync(Func<Session?> getSession, CancellationToken ct) =>
        new SocksServer(getSession, Credentials).RunAsync(listener, ct);

    public static Session? SelectClientSession()
    {
        var channels = Session.Active.Values.Where(s => s.IsClient && s.Ready && s.InitiatesStreams).ToArray();
        if (channels.Length > 1)
        {
            // Never silently route a request through a different remote server.
            Diagnostics.Write($"client-proxy-ambiguous channels={channels.Length}");
            throw new IOException("More than one RDP proxy channel is ready. Keep one server adapter connected in ClientProxy mode.");
        }
        return channels.SingleOrDefault();
    }

    public void Dispose()
    {
        listener.Stop();
        Credentials.Dispose();
    }
}
