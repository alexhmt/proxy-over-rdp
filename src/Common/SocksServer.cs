using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RdpProxy;

internal sealed class SocksServer(Func<Session?> getSession, Credentials credentials)
{
    private readonly SemaphoreSlim slots = new(128, 128);

    public async Task RunAsync(TcpListener listener, CancellationToken ct)
    {
        var handlers = new HashSet<Task>();
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken running = runStop.Token;
        try
        {
            while (!running.IsCancellationRequested)
            {
                TcpClient socket = await listener.AcceptTcpClientAsync(running);
                if (!slots.Wait(0)) { socket.Dispose(); continue; }
                handlers.RemoveWhere(task => task.IsCompleted);
                handlers.Add(HandleAsync(socket, running));
            }
        }
        finally
        {
            runStop.Cancel();
            listener.Stop();
            await Task.WhenAll(handlers);
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken ct)
    {
        using (socket)
        {
            ProxyStream? stream = null;
            bool requestReceived = false;
            bool accepted = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(10000);
            try
            {
                socket.NoDelay = true;
                NetworkStream network = socket.GetStream();
                byte[] greeting = await ReadAsync(network, 2, timeout.Token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                byte[] methods = await ReadAsync(network, greeting[1], timeout.Token);
                if (!methods.Contains((byte)2))
                {
                    await network.WriteAsync(new byte[] { 5, 255 }, timeout.Token);
                    return;
                }
                await network.WriteAsync(new byte[] { 5, 2 }, timeout.Token);
                byte[] auth = await ReadAsync(network, 2, timeout.Token);
                if (auth[0] != 1 || auth[1] == 0) return;
                byte[] username = await ReadAsync(network, auth[1], timeout.Token);
                byte[] passwordLength = await ReadAsync(network, 1, timeout.Token);
                byte[] password = await ReadAsync(network, passwordLength[0], timeout.Token);
                bool valid = credentials.Validate(username, password);
                Array.Clear(password);
                await network.WriteAsync(new byte[] { 1, valid ? (byte)0 : (byte)1 }, timeout.Token);
                if (!valid) return;
                byte[] request = await ReadAsync(network, 4, timeout.Token);
                if (request[0] != 5 || request[2] != 0) return;
                requestReceived = true;
                if (request[1] != 1) throw new SocksException(7);
                string host;
                switch (request[3])
                {
                    case 1: host = new IPAddress(await ReadAsync(network, 4, timeout.Token)).ToString(); break;
                    case 4: host = new IPAddress(await ReadAsync(network, 16, timeout.Token)).ToString(); break;
                    case 3:
                        int length = (await ReadAsync(network, 1, timeout.Token))[0];
                        if (length == 0) throw new SocksException(4);
                        byte[] name = await ReadAsync(network, length, timeout.Token);
                        if (name.Any(value => value is < 33 or > 126)) throw new SocksException(4);
                        host = System.Text.Encoding.ASCII.GetString(name);
                        break;
                    default: throw new SocksException(8);
                }
                byte[] portBytes = await ReadAsync(network, 2, timeout.Token);
                int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
                if (port == 0) throw new SocksException(1);
                timeout.Token.ThrowIfCancellationRequested();
                Session session = getSession() ?? throw new SocksException(1);
                stream = session.Open(socket, host, port);
                // DNS + TCP gets a separate ten-second budget after the SOCKS handshake.
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(10000);
                byte[] bound = await stream.Opened.WaitAsync(connect.Token);
                var response = new byte[3 + bound.Length];
                response[0] = 5;
                bound.CopyTo(response, 3);
                await network.WriteAsync(response, connect.Token);
                accepted = true;
                using var stopStream = ct.Register(stream.Dispose);
                await stream.RunAsync();
            }
            catch (Exception ex)
            {
                if (requestReceived && !accepted && !ct.IsCancellationRequested)
                {
                    try
                    {
                        using var reply = new CancellationTokenSource(1000);
                        await socket.GetStream().WriteAsync(new byte[] { 5, SocksException.CodeFor(ex), 0, 1, 0, 0, 0, 0, 0, 0 }, reply.Token);
                    }
                    catch { }
                }
                // Do not log credentials, hostnames or arbitrary peer-controlled exception text.
                if (!ct.IsCancellationRequested)
                {
                    Diagnostics.Fault($"socks-handler requestReceived={requestReceived} accepted={accepted}", ex);
                    Console.Error.WriteLine($"SOCKS closed: {ex.GetType().Name}");
                }
            }
            finally
            {
                if (stream != null && !accepted)
                {
                    stream.Reset();
                }
                stream?.Dispose();
                slots.Release();
            }
        }
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int size, CancellationToken ct)
    {
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
