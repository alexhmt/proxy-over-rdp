using System.Collections.Concurrent;
using System.Net.Sockets;

namespace RdpProxy;

internal sealed class Session : IDisposable
{
    private const int MaxCreditBurst = 8;
    private readonly object gate = new();
    private readonly object receiveGate = new();
    private readonly object wakeGate = new();
    private readonly Dictionary<ulong, ProxyStream> streams = new();
    private readonly Queue<Frame> controls = new();
    private readonly LinkedList<PendingCredit> credits = new();
    private readonly Dictionary<ProxyStream, LinkedListNode<PendingCredit>> creditsByStream = new();
    private readonly FrameDecoder decoder = new();
    private readonly Action<Frame> receiveFrame;
    private readonly DataReceiver receiveData;
    private readonly SemaphoreSlim wake = new(0, 1);
    private ProxyStream[] streamSnapshot = Array.Empty<ProxyStream>();
    private ProxyStream? deferredStream;
    private Frame? deferredFrame;
    private ulong nextStream;
    private ulong lastRemoteStream;
    private int cursor;
    private int creditBurst;
    private int disposed;
    private long lastReceived = Environment.TickCount64;
    private long nextPing = Environment.TickCount64 + 3000;
    private long sentBytes;
    private long receivedBytes;
    private long receivedFrames;
    private long nativeReadBytes;
    private long nativeWriteBytes;
    private long lastNativeRead = Environment.TickCount64;
    private long writeStarted;
    private long lastWriteCompleted = Environment.TickCount64;
    private int writePending;
    private int lastKind;
    private long nextDiagnostic = Environment.TickCount64 + 30000;
    private readonly Timer diagnosticWatch;
    private volatile bool ready;
    public static readonly ConcurrentDictionary<string, Session> Active = new();
    private static readonly ConcurrentDictionary<Task, byte> connectorOperations = new();
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; }
    public bool IsClient { get; }
    public ProxyMode Mode { get; }
    public bool InitiatesStreams => Mode == ProxyMode.ClientProxy ? IsClient : !IsClient;
    public bool Ready => ready && !Stop.IsCancellationRequested;
    public CancellationTokenSource Stop { get; } = new();
    public TransferMetrics Metrics { get; } = new();
    public WaitHandle OutputReady => wake.AvailableWaitHandle;

    private sealed class PendingCredit(ProxyStream stream, int count, long queuedAt)
    {
        public ProxyStream Stream { get; } = stream;
        public int Count { get; set; } = count;
        public long QueuedAt { get; } = queuedAt;
    }

    public Session(bool isClient, ProxyMode mode = ProxyMode.ServerProxy, string? ownerId = null)
    {
        OwnerId = ownerId ?? Id;
        IsClient = isClient;
        Mode = mode;
        receiveFrame = Process;
        receiveData = ProcessData;
        diagnosticWatch = new Timer(_ =>
        {
            if (Volatile.Read(ref disposed) == 0 &&
                (WritePendingMs > 5000 || Environment.TickCount64 - Interlocked.Read(ref lastReceived) > 12000))
                Diagnostics.Write("watch " + Describe());
        }, null, 5000, 5000);
        Diagnostics.Write($"channel-created owner={OwnerId} channel={Id} side={(IsClient ? "Client" : "Server")}");
        Active[Id] = this;
        byte role = (byte)(isClient ? 1 : 2);
        // Keep the original one-byte HELLO in the default direction for v3/v4 peers.
        byte[] hello = mode == ProxyMode.ServerProxy ? new[] { role } : new[] { role, (byte)mode };
        Queue(new Frame(Kind.Hello, 0, hello));
    }

    public object Status()
    {
        lock (gate) return new { OwnerId, ChannelId = Id, Connected = Ready, Mode = Mode.ToString(), InitiatesStreams, Streams = streams.Count,
            SentBytes = Interlocked.Read(ref sentBytes), ReceivedBytes = Interlocked.Read(ref receivedBytes),
            ReceiveIdleMs = Environment.TickCount64 - Interlocked.Read(ref lastReceived),
            NativeWritePendingMs = WritePendingMs, ReceivedFrames = Interlocked.Read(ref receivedFrames),
            Timings = Metrics.Snapshot() };
    }

    private long WritePendingMs => Volatile.Read(ref writePending) == 0 ? 0 :
        Environment.TickCount64 - Interlocked.Read(ref writeStarted);

    public string Describe()
    {
        long now = Environment.TickCount64;
        lock (gate) return $"owner={OwnerId} channel={Id} side={(IsClient ? "Client" : "Server")} mode={Mode} initiator={InitiatesStreams} ready={Ready} streams={streams.Count} controls={controls.Count} credits={credits.Count}" +
            $" rxIdleMs={now - Interlocked.Read(ref lastReceived)} nativeRxIdleMs={now - Interlocked.Read(ref lastNativeRead)}" +
            $" txIdleMs={now - Interlocked.Read(ref lastWriteCompleted)} writePendingMs={WritePendingMs}" +
            $" frames={Interlocked.Read(ref receivedFrames)} lastRxKind={(Kind)Volatile.Read(ref lastKind)}" +
            $" nativeRxBytes={Interlocked.Read(ref nativeReadBytes)} nativeTxBytes={Interlocked.Read(ref nativeWriteBytes)}" +
            $" payloadQueued={Interlocked.Read(ref sentBytes)} payloadReceived={Interlocked.Read(ref receivedBytes)}";
    }

    public void NativeRead(int count)
    {
        Interlocked.Add(ref nativeReadBytes, count);
        Interlocked.Exchange(ref lastNativeRead, Environment.TickCount64);
    }

    public void NativeWriteStarted()
    {
        Interlocked.Exchange(ref writeStarted, Environment.TickCount64);
        Volatile.Write(ref writePending, 1);
    }

    public void NativeWriteCompleted(int count)
    {
        Interlocked.Add(ref nativeWriteBytes, count);
        Interlocked.Exchange(ref lastWriteCompleted, Environment.TickCount64);
        Volatile.Write(ref writePending, 0);
    }

    public void Notify()
    {
        // Serialize producers separately: taking gate here would invert stream/session locks.
        // Waiters can only consume a signal, so they cannot make this release overflow.
        lock (wakeGate)
        {
            if (wake.CurrentCount == 0) wake.Release();
        }
    }

    public async Task WaitAsync(CancellationToken ct) => await wake.WaitAsync(1000, ct);

    // Consume before scanning queues so a producer racing the scan leaves a signal.
    public void ConsumeOutputSignal() => wake.Wait(0);

    public void Queue(Frame frame)
    {
        lock (gate)
        {
            if (Stop.IsCancellationRequested) throw new IOException("DVC disconnected.");
            if (controls.Count >= 4096) throw new IOException("Control queue full.");
            frame.QueuedAt = Metrics.Timestamp();
            controls.Enqueue(frame);
            // The RESET caller disposes its stream after Queue returns. Stop selecting
            // its DATA/FIN or credits now, before the writer can send this RESET.
            if (frame.Kind == Kind.Reset && streams.TryGetValue(frame.StreamId, out var stream))
                Remove(stream);
        }
        Notify();
    }

    public void ReturnCredit(ProxyStream stream, int count)
    {
        lock (gate)
        {
            if (Stop.IsCancellationRequested || !IsActive(stream)) return;
            if (count <= 0 || count > Protocol.Window) throw new IOException("Invalid returned credit.");
            if (creditsByStream.TryGetValue(stream, out var node))
            {
                if (count > Protocol.Window - node.Value.Count) throw new IOException("Returned credit exceeds window.");
                node.Value.Count += count;
            }
            else
            {
                var credit = new PendingCredit(stream, count, Metrics.Timestamp());
                creditsByStream.Add(stream, credits.AddLast(credit));
            }
        }
        Notify();
    }

    public bool HasPending
    {
        get
        {
            ProxyStream[] items;
            lock (gate)
            {
                if (controls.Count != 0 || credits.Count != 0 || deferredFrame != null) return true;
                items = streamSnapshot;
            }
            foreach (var stream in items)
                if (stream.HasPending) return true;
            return false;
        }
    }

    public bool TryTake(out Frame frame)
    {
        ProxyStream[] items;
        lock (gate)
        {
            if (controls.TryDequeue(out frame!)) return true;
            if (creditBurst < MaxCreditBurst && TryTakeCredit(out frame!)) return true;
            if (deferredFrame != null)
            {
                frame = deferredFrame;
                deferredFrame = null;
                deferredStream = null;
                creditBurst = 0;
                return true;
            }
            items = streamSnapshot;
        }
        // Do not hold the session lock while entering a stream lock: receive callbacks
        // may queue control records in the opposite direction.
        for (int i = 0; i < items.Length; i++)
        {
            var stream = items[(int)((uint)cursor++ % (uint)items.Length)];
            if (!stream.TryTake(out var candidate)) continue;
            lock (gate)
            {
                if (!IsActive(stream)) continue;
                // OPEN_OK can be queued after the first check but before TryTake.
                // Keep this candidate ahead of later DATA/FIN while controls drain.
                if (controls.TryDequeue(out frame!))
                {
                    deferredStream = stream;
                    deferredFrame = candidate;
                    return true;
                }
                frame = candidate;
                creditBurst = 0;
                return true;
            }
        }
        lock (gate)
        {
            if (controls.TryDequeue(out frame!)) return true;
            // A complete scan found no DATA/FIN, so start another bounded credit burst.
            creditBurst = 0;
            if (TryTakeCredit(out frame!)) return true;
        }
        frame = null!;
        return false;
    }

    // These helpers are called only while holding gate; neither enters a stream lock.
    private bool IsActive(ProxyStream stream) =>
        streams.TryGetValue(stream.Id, out var active) && ReferenceEquals(active, stream);

    private bool TryTakeCredit(out Frame frame)
    {
        if (credits.First is not { } node)
        {
            frame = null!;
            return false;
        }
        PendingCredit credit = node.Value;
        credits.RemoveFirst();
        creditsByStream.Remove(credit.Stream);
        frame = new Frame(Kind.WindowUpdate, credit.Stream.Id, Protocol.Credit(credit.Count))
        {
            QueuedAt = credit.QueuedAt
        };
        creditBurst = Math.Min(creditBurst + 1, MaxCreditBurst);
        return true;
    }

    public ProxyStream Open(TcpClient socket, string host, int port)
    {
        ProxyStream? stream = null;
        try
        {
            lock (gate)
            {
                if (!InitiatesStreams || !Ready) throw new IOException("RDP channel unavailable.");
                if (streams.Count >= Protocol.MaxStreams) throw new IOException("Stream limit reached.");
                stream = new ProxyStream(this, ++nextStream, socket);
                streams.Add(stream.Id, stream);
                streamSnapshot = streams.Values.ToArray();
                Queue(new Frame(Kind.Open, stream.Id, Protocol.Target(host, port)));
                return stream;
            }
        }
        catch
        {
            // Dispose may enter the stream lock; never do that with gate held.
            if (stream != null)
            {
                Remove(stream);
                stream.Dispose();
            }
            throw;
        }
    }

    public void Remove(ProxyStream stream)
    {
        lock (gate)
        {
            if (!IsActive(stream)) return;
            streams.Remove(stream.Id);
            streamSnapshot = streams.Values.ToArray();
            if (creditsByStream.Remove(stream, out var credit)) credits.Remove(credit);
            if (ReferenceEquals(deferredStream, stream))
            {
                deferredStream = null;
                deferredFrame = null;
            }
        }
    }

    public void Receive(ReadOnlySpan<byte> bytes)
    {
        lock (receiveGate) decoder.Push(bytes, receiveFrame, receiveData);
    }

    private void RecordReceived(Kind kind)
    {
        Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
        Interlocked.Increment(ref receivedFrames);
        Volatile.Write(ref lastKind, (int)kind);
    }

    private void ProcessData(ulong streamId, ReadOnlySpan<byte> bytes)
    {
        RecordReceived(Kind.Data);
        if (!Ready) throw new IOException("Frame before HELLO.");
        if (streamId == 0) throw new IOException("Missing stream ID.");
        ProxyStream? found;
        lock (gate) streams.TryGetValue(streamId, out found);
        if (found == null)
        {
            Queue(Protocol.Control(Kind.Reset, streamId));
            return;
        }
        found.AcceptData(bytes);
    }

    private void Process(Frame frame)
    {
        RecordReceived(frame.Kind);
        if (frame.Kind == Kind.Hello)
        {
            if (ready || frame.StreamId != 0 || frame.Body.Length is not (1 or 2) || frame.Body[0] != (IsClient ? 2 : 1))
                throw new IOException("Invalid HELLO.");
            var peerMode = frame.Body.Length == 1 ? ProxyMode.ServerProxy : (ProxyMode)frame.Body[1];
            if (!Enum.IsDefined(peerMode) || peerMode != Mode)
                throw new IOException($"Proxy mode mismatch. Start both endpoints with the same --mode ({Mode}).");
            ready = true;
            Console.WriteLine($"DVC connected: {Id}");
            Diagnostics.Write("hello-complete " + Describe());
            return;
        }
        if (!Ready) throw new IOException("Frame before HELLO.");
        if (frame.Kind is Kind.Ping or Kind.Pong)
        {
            if (frame.StreamId != 0 || frame.Body.Length != 0) throw new IOException("Invalid heartbeat.");
            if (frame.Kind == Kind.Ping) Queue(Protocol.Control(Kind.Pong));
            return;
        }
        if (frame.StreamId == 0) throw new IOException("Missing stream ID.");
        if (frame.Kind == Kind.Open)
        {
            if (InitiatesStreams || frame.StreamId <= lastRemoteStream) throw new IOException("Invalid OPEN sequence.");
            lastRemoteStream = frame.StreamId;
            var target = Protocol.ReadTarget(frame.Body);
            ProxyStream stream;
            TaskCompletionSource completion;
            lock (gate)
            {
                if (Stop.IsCancellationRequested) throw new IOException("DVC disconnected.");
                if (streams.Count >= Protocol.MaxStreams)
                {
                    Queue(new Frame(Kind.OpenError, frame.StreamId, new byte[] { 1 }));
                    return;
                }
                stream = new ProxyStream(this, frame.StreamId, new TcpClient());
                streams.Add(stream.Id, stream);
                streamSnapshot = streams.Values.ToArray();
                // Register before releasing gate: shutdown cannot miss a connector
                // that is about to start after this session has been disposed.
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connectorOperations.TryAdd(completion.Task, 0);
            }
            _ = RunConnectorAsync(stream, target.Host, target.Port, completion);
            return;
        }
        ProxyStream? found;
        lock (gate) streams.TryGetValue(frame.StreamId, out found);
        // Late records can cross a RESET. Never respond to RESET with RESET.
        if (found == null)
        {
            if (frame.Kind != Kind.Reset) Queue(Protocol.Control(Kind.Reset, frame.StreamId));
            return;
        }
        found.Accept(frame);
    }

    private static async Task RunConnectorAsync(ProxyStream stream, string host, int port,
        TaskCompletionSource completion)
    {
        try { await stream.ConnectAsync(host, port); }
        catch (Exception ex)
        {
            Diagnostics.Fault("connector-task", ex);
            stream.Dispose();
        }
        finally
        {
            connectorOperations.TryRemove(completion.Task, out _);
            completion.TrySetResult();
        }
    }

    // The caller has first stopped channel admission and disposed all sessions.
    // The registry also covers connector tasks of sessions already removed from Active.
    public static Task DrainConnectionsAsync() => Task.WhenAll(connectorOperations.Keys.ToArray());

    public void Tick()
    {
        long now = Environment.TickCount64;
        if (now >= nextDiagnostic)
        {
            nextDiagnostic = now + 30000;
            Diagnostics.Write("periodic " + Describe());
        }
        if (now - Interlocked.Read(ref lastReceived) > 15000)
        {
            Diagnostics.Write("heartbeat-expired " + Describe());
            throw new IOException("DVC heartbeat expired.");
        }
        if (Ready && now >= nextPing)
        {
            nextPing = now + 3000;
            Queue(Protocol.Control(Kind.Ping));
        }
    }

    public void CountSent(int count) => Interlocked.Add(ref sentBytes, count);
    public void CountReceived(int count) => Interlocked.Add(ref receivedBytes, count);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Diagnostics.Write("channel-dispose " + Describe());
        diagnosticWatch.Dispose();
        ready = false;
        Stop.Cancel();
        ProxyStream[] items;
        lock (gate)
        {
            items = streamSnapshot;
            streams.Clear();
            streamSnapshot = Array.Empty<ProxyStream>();
            controls.Clear();
            credits.Clear();
            creditsByStream.Clear();
            deferredStream = null;
            deferredFrame = null;
        }
        foreach (var stream in items) stream.Dispose();
        Active.TryRemove(Id, out _);
        Notify();
        Console.WriteLine($"DVC closed: {Id}");
    }
}
