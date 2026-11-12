using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RdpProxy;

internal sealed class ProxyStream : IDisposable
{
    private readonly Session session;
    private readonly TcpClient socket;
    private readonly CancellationTokenSource stop;
    private readonly CancellationToken token;
    private readonly object gate = new();
    private readonly Queue<Frame> outbound = new();
    private readonly SemaphoreSlim outboundSpace = new(256, 256);
    private readonly SemaphoreSlim creditChanged = new(0, 1);
    private byte[] inbound = Array.Empty<byte>();
    private readonly SemaphoreSlim inboundChanged = new(0, 1);
    private int inboundHead;
    private int inboundCount;
    private readonly TaskCompletionSource<byte[]> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int sendCredit = Protocol.Window;
    private int receiveCredit = Protocol.Window;
    private bool remoteFin;
    private bool connected;
    private int disposed;
    public ulong Id { get; }
    public Task<byte[]> Opened => opened.Task;
    public bool HasPending { get { lock (gate) return outbound.Count != 0; } }

    private string Describe()
    {
        lock (gate) return $"channel={session.Id} stream={Id} connected={connected} remoteFin={remoteFin}" +
            $" sendCredit={sendCredit} receiveCredit={receiveCredit} buffered={inboundCount} outbound={outbound.Count}";
    }

    public void Reset()
    {
        Diagnostics.Write("stream-local-reset " + Describe());
        if (!session.Stop.IsCancellationRequested)
        {
            try { session.Queue(Protocol.Control(Kind.Reset, Id)); } catch { session.Dispose(); }
        }
        Dispose();
    }

    public ProxyStream(Session session, ulong id, TcpClient socket)
    {
        this.session = session;
        this.socket = socket;
        Id = id;
        stop = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
        token = stop.Token;
    }

    public bool TryTake(out Frame frame)
    {
        lock (gate)
        {
            if (!outbound.TryDequeue(out frame!)) return false;
            outboundSpace.Release();
            return true;
        }
    }

    public void Accept(Frame frame)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            switch (frame.Kind)
            {
                case Kind.OpenOk:
                    if (!session.InitiatesStreams || connected || opened.Task.IsCompleted ||
                        !(frame.Body.Length == 7 && frame.Body[0] == 1 || frame.Body.Length == 19 && frame.Body[0] == 4))
                        throw new IOException("Invalid OPEN_OK.");
                    connected = true;
                    opened.TrySetResult(frame.Body);
                    break;
                case Kind.OpenError:
                    if (!session.InitiatesStreams || connected || frame.Body.Length != 1 || frame.Body[0] is < 1 or > 8)
                        throw new IOException("Invalid OPEN_ERROR.");
                    // SOCKS handler owns this accepted socket until it has sent the failure reply.
                    opened.TrySetException(new SocksException(frame.Body[0]));
                    break;
                case Kind.WindowUpdate:
                    if (!connected || frame.Body.Length != 4) throw new IOException("Invalid window update.");
                    int credit = BinaryPrimitives.ReadInt32BigEndian(frame.Body);
                    if (credit <= 0 || credit > Protocol.Window - sendCredit) throw new IOException("Invalid credit.");
                    sendCredit += credit;
                    // All producers hold gate; waits can only consume the pending signal.
                    if (creditChanged.CurrentCount == 0) creditChanged.Release();
                    break;
                case Kind.Fin:
                    if (!connected || remoteFin || frame.Body.Length != 0) throw new IOException("Invalid FIN.");
                    Diagnostics.Write("stream-peer-fin " + Describe());
                    remoteFin = true;
                    SignalInbound();
                    break;
                case Kind.Reset:
                    Diagnostics.Write("stream-peer-reset " + Describe());
                    if (frame.Body.Length != 0) throw new IOException("Invalid RESET.");
                    Dispose();
                    break;
                default: throw new IOException("Unexpected stream frame.");
            }
        }
    }

    public void AcceptData(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (!connected || remoteFin || data.Length == 0 || data.Length > receiveCredit)
                throw new IOException("Invalid DATA or receive window exceeded.");
            EnsureInboundCapacity(inboundCount + data.Length);
            receiveCredit -= data.Length;
            int tail = (inboundHead + inboundCount) % inbound.Length;
            int first = Math.Min(data.Length, inbound.Length - tail);
            data[..first].CopyTo(inbound.AsSpan(tail));
            data[first..].CopyTo(inbound);
            inboundCount += data.Length;
            SignalInbound();
            session.CountReceived(data.Length);
        }
    }

    private void EnsureInboundCapacity(int required)
    {
        if (required <= inbound.Length) return;
        if (required > Protocol.Window) throw new IOException("Receive buffer exceeded.");
        int capacity = Math.Max(Protocol.BlockSize, inbound.Length);
        while (capacity < required) capacity = Math.Min(capacity * 2, Protocol.Window);
        var larger = new byte[capacity];
        int first = Math.Min(inboundCount, inbound.Length - inboundHead);
        inbound.AsSpan(inboundHead, first).CopyTo(larger);
        inbound.AsSpan(0, inboundCount - first).CopyTo(larger.AsSpan(first));
        inbound = larger;
        inboundHead = 0;
    }

    public async Task ConnectAsync(string host, int port)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(10000);
            // Resolve only on the connector endpoint selected by Mode; never on the SOCKS side.
            IPAddress[] addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } :
                await Dns.GetHostAddressesAsync(host, timeout.Token);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
            await socket.ConnectAsync(addresses, port, timeout.Token);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                connected = true;
                session.Queue(new Frame(Kind.OpenOk, Id, Protocol.BoundAddress((IPEndPoint)socket.Client.LocalEndPoint!)));
            }
            await RunAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Fault("connect-failed " + Describe(), ex);
            if (!session.Stop.IsCancellationRequested && Volatile.Read(ref disposed) == 0)
            {
                try { session.Queue(new Frame(Kind.OpenError, Id, new[] { SocksException.CodeFor(ex) })); }
                catch { session.Dispose(); }
            }
            Dispose();
        }
    }

    public async Task RunAsync()
    {
        Task reader = Task.CompletedTask;
        Task writer = Task.CompletedTask;
        try
        {
            socket.NoDelay = true;
            reader = ReadSocketAsync();
            writer = WriteSocketAsync();
            Task first = await Task.WhenAny(reader, writer);
            await first;
            await Task.WhenAll(reader, writer);
        }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested)
            {
                Diagnostics.Fault("stream-pump-failed " + Describe() + $" reader={reader.Status} writer={writer.Status}", ex);
                Console.Error.WriteLine($"Stream {Id}: {ex.GetType().Name}; closing. See diagnostic log.");
                Reset();
            }
        }
        finally
        {
            Dispose();
            try { await Task.WhenAll(reader, writer); } catch { }
        }
    }

    private async Task EnqueueAsync(Frame frame)
    {
        await outboundSpace.WaitAsync(token);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            frame.QueuedAt = session.Metrics.Timestamp();
            outbound.Enqueue(frame);
        }
        session.Notify();
    }

    private async Task ReadSocketAsync()
    {
        var buffer = new byte[Protocol.BlockSize];
        NetworkStream network = socket.GetStream();
        while (true)
        {
            int allowance;
            long creditWaitStarted = 0;
            try
            {
                while (true)
                {
                    lock (gate) allowance = Math.Min(sendCredit, buffer.Length);
                    if (allowance > 0) break;
                    if (creditWaitStarted == 0) creditWaitStarted = session.Metrics.Timestamp();
                    await creditChanged.WaitAsync(token);
                }
            }
            finally { session.Metrics.RecordCreditWait(creditWaitStarted); }
            int count;
            try { count = await network.ReadAsync(buffer.AsMemory(0, allowance), token); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Diagnostics.Fault("socket-read-failed " + Describe(), ex);
                throw;
            }
            if (count == 0)
            {
                Diagnostics.Write("socket-read-eof " + Describe());
                var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await EnqueueAsync(Protocol.Control(Kind.Fin, Id) with { Written = () => written.TrySetResult(true) });
                await written.Task.WaitAsync(token);
                return;
            }
            lock (gate) sendCredit -= count;
            await EnqueueAsync(new Frame(Kind.Data, Id, buffer.AsSpan(0, count).ToArray()));
            session.CountSent(count);
        }
    }

    private void SignalInbound()
    {
        // Every caller holds gate, serializing the check with other producers.
        if (inboundChanged.CurrentCount == 0) inboundChanged.Release();
    }

    private async Task WriteSocketAsync()
    {
        byte[] buffer = new byte[Protocol.BlockSize];
        while (true)
        {
            int count;
            bool finished;
            lock (gate)
            {
                count = Math.Min(inboundCount, buffer.Length);
                finished = remoteFin && count == 0;
                if (count != 0)
                {
                    int first = Math.Min(count, inbound.Length - inboundHead);
                    inbound.AsSpan(inboundHead, first).CopyTo(buffer);
                    inbound.AsSpan(0, count - first).CopyTo(buffer.AsSpan(first));
                    inboundHead = (inboundHead + count) % inbound.Length;
                    inboundCount -= count;
                }
            }
            if (finished)
            {
                socket.Client.Shutdown(SocketShutdown.Send);
                return;
            }
            if (count == 0)
            {
                await inboundChanged.WaitAsync(token);
                continue;
            }
            try { await socket.GetStream().WriteAsync(buffer.AsMemory(0, count), token); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Diagnostics.Fault("socket-write-failed " + Describe(), ex);
                throw;
            }
            lock (gate) receiveCredit += count;
            session.ReturnCredit(this, count);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        // Before OPEN_OK, leave the initiating endpoint's accepted socket to SOCKS
        // so it can return a failure REP. Connector sockets are always owned here.
        if (!session.InitiatesStreams || connected) socket.Dispose();
        stop.Dispose();
        opened.TrySetException(new IOException("Stream closed."));
        // Session never enters a stream lock while holding its own gate.
        session.Remove(this);
    }
}

internal sealed class SocksException(byte reply) : IOException("SOCKS connection failed.")
{
    public byte Reply { get; } = reply;
    public static byte CodeFor(Exception error) => error switch
    {
        SocksException socks => socks.Reply,
        OperationCanceledException => 6,
        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => 5,
            SocketError.HostNotFound or SocketError.NoData or SocketError.HostUnreachable => 4,
            SocketError.NetworkUnreachable => 3,
            SocketError.AccessDenied => 2,
            SocketError.TimedOut => 6,
            _ => 1
        },
        _ => 1
    };
}
