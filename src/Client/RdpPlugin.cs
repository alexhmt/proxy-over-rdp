using System.Runtime.InteropServices;

namespace RdpProxy.Client;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
[Guid("6F75C6EF-33F1-4F21-AE88-9EB09F65B8F1")]
public sealed class RdpPlugin : IWTSPlugin, IWTSListenerCallback
{
    public static readonly Guid PluginClsid = new("6F75C6EF-33F1-4F21-AE88-9EB09F65B8F1");
    internal static ProxyMode Mode { get; set; } = ProxyMode.ServerProxy;
    private const int Unavailable = unchecked((int)0x80010108);
    private static readonly object processGate = new();
    private static readonly HashSet<RdpPlugin> plugins = new();
    // Disconnected channels stay here until both their writer and native Close
    // have returned. An owner's active-channel collection is not a drain list.
    private static readonly HashSet<ClientChannel> pendingChannels = new();
    private static bool stopping;
    private static int initializations;
    private static TaskCompletionSource? initializationsDrained;
    private readonly string ownerId = Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private readonly HashSet<ClientChannel> channels = new();
    private IWTSListener? listener;
    private bool initialized;
    private bool accepting;
    private bool terminated;

    public int Initialize(IWTSVirtualChannelManager manager)
    {
        lock (processGate)
        {
            if (stopping) return Unavailable;
            lock (gate)
            {
                // A COM factory creates one plugin instance per RDP control/connection.
                // Never replace another manager's listener inside a live instance.
                if (initialized || terminated) return unchecked((int)0x8000FFFF);
                initialized = true;
                accepting = true;
            }
            plugins.Add(this);
            if (initializations++ == 0)
                initializationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            // Native calls stay outside gate: CreateListener may invoke a callback.
            int result = manager.CreateListener(Protocol.ChannelName, 0, this, out var created);
            bool retained;
            lock (processGate)
            {
                lock (gate)
                {
                    retained = !stopping && !terminated && result >= 0;
                    if (retained) listener = created;
                }
            }
            Diagnostics.Write($"plugin-initialize owner={ownerId} listenerHr=0x{result:X8}");
            if (result < 0) Clear(false, "initialize-failed");
            return result < 0 || retained ? result : Unavailable;
        }
        catch (Exception ex)
        {
            Clear(false, "initialize-failed");
            Diagnostics.Fault($"plugin-initialize owner={ownerId}", ex);
            Console.Error.WriteLine(ex.Message);
            return Marshal.GetHRForException(ex);
        }
        finally
        {
            lock (processGate)
            {
                if (--initializations == 0) initializationsDrained!.TrySetResult();
            }
        }
    }

    public int Connected()
    {
        lock (processGate)
        {
            if (stopping) return Unavailable;
            lock (gate)
            {
                if (terminated || !initialized) return unchecked((int)0x8000FFFF);
                accepting = true;
            }
        }
        Diagnostics.Write($"plugin-connected owner={ownerId}");
        return 0;
    }

    public int Disconnected(uint code)
    {
        Diagnostics.Write($"plugin-disconnected owner={ownerId} code={code} hex=0x{code:X8}");
        Clear(false, "disconnected");
        return 0;
    }

    public int Terminated()
    {
        Diagnostics.Write($"plugin-terminated owner={ownerId}");
        Clear(true, "terminated");
        return 0;
    }

    private void Clear(bool terminate, string reason)
    {
        ClientChannel[] old;
        lock (gate)
        {
            accepting = false;
            if (terminate) { terminated = true; listener = null; }
            old = channels.ToArray();
            channels.Clear();
        }
        if (terminate)
        {
            lock (processGate) plugins.Remove(this);
        }
        // Only this RDP connection's channels. Never clear Session.Active globally.
        Diagnostics.Write($"plugin-clear owner={ownerId} reason={reason} channels={old.Length}");
        foreach (var channel in old) channel.Stop();
    }

    private void ChannelClosed(ClientChannel channel)
    {
        lock (gate) channels.Remove(channel);
    }

    internal static void StopAccepting()
    {
        lock (processGate) stopping = true;
    }

    internal static async Task StopAllAsync()
    {
        RdpPlugin[] owners;
        ClientChannel[] pending;
        Task initializing;
        lock (processGate)
        {
            stopping = true;
            owners = plugins.ToArray();
            pending = pendingChannels.ToArray();
            initializing = initializationsDrained?.Task ?? Task.CompletedTask;
        }
        // Never call native code, dispose sessions, or enter a channel lock while
        // holding processGate or an owner's gate. Native Close can call us back.
        foreach (var owner in owners) owner.Clear(true, "process-exit");
        foreach (var channel in pending) channel.Stop();
        await Task.WhenAll(pending.Select(channel => channel.Completion).Append(initializing)).ConfigureAwait(false);
    }

    private static void ChannelDrained(ClientChannel channel)
    {
        lock (processGate) pendingChannels.Remove(channel);
    }

    public int OnNewChannelConnection(IWTSVirtualChannel channel, string data, out bool accept, out IWTSVirtualChannelCallback callback)
    {
        accept = false;
        callback = null!;
        try
        {
            ClientChannel connection;
            lock (processGate)
            {
                if (stopping) return 0;
                lock (gate)
                {
                    // Admission is serialized with both owner disconnect and Exit.
                    if (!accepting || terminated) return 0;
                    connection = new ClientChannel(channel, ownerId, ChannelClosed, ChannelDrained);
                    channels.Add(connection);
                    pendingChannels.Add(connection);
                    callback = connection;
                    accept = true;
                }
            }
            connection.ArmHandshakeDeadline();
            Diagnostics.Write($"plugin-new-channel owner={ownerId}");
            return 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Fault($"plugin-new-channel owner={ownerId}", ex);
            return Marshal.GetHRForException(ex);
        }
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ClientChannel : IWTSVirtualChannelCallback
{
    private readonly IWTSVirtualChannel channel;
    private readonly Session session;
    private readonly Action<ClientChannel> onClosed;
    private readonly Action<ClientChannel> onDrained;
    private readonly Timer handshakeDeadline;
    private readonly object receiveGate = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] receiveBuffer = Array.Empty<byte>();
    private Task? writerTask;
    private int closed;
    public bool Closed => Volatile.Read(ref closed) != 0;
    internal Task Completion => completion.Task;
    internal ClientChannel(IWTSVirtualChannel channel, string ownerId,
        Action<ClientChannel> onClosed, Action<ClientChannel> onDrained)
    {
        this.channel = channel;
        this.onClosed = onClosed;
        this.onDrained = onDrained;
        session = new Session(true, RdpPlugin.Mode, ownerId);
        handshakeDeadline = new Timer(_ => Stop(helloExpired: true), null, Timeout.Infinite, Timeout.Infinite);
        // The server always sends HELLO first. Start the writer on that receive,
        // after native channel admission, rather than racing the accept callback.
    }

    internal void ArmHandshakeDeadline()
    {
        lock (receiveGate)
        {
            if (!Closed && writerTask == null) handshakeDeadline.Change(15000, Timeout.Infinite);
        }
    }
    public int OnDataReceived(uint size, IntPtr buffer)
    {
        try
        {
            lock (receiveGate)
            {
                if (Closed) return unchecked((int)0x80004005);
                if (size > 65536) throw new IOException("Oversized native DVC record.");
                int count = checked((int)size);
                if (receiveBuffer.Length < count)
                    receiveBuffer = new byte[Math.Max(Protocol.MaxEncodedFrame, count)];
                Marshal.Copy(buffer, receiveBuffer, 0, count);
                session.NativeRead(count);
                // Receive consumes the span synchronously. Serialize the copy too,
                // so another callback cannot overwrite this channel's scratch buffer.
                session.Receive(receiveBuffer.AsSpan(0, count));
                if (session.Ready && writerTask == null)
                {
                    handshakeDeadline.Dispose();
                    // Stop uses the same gate: it cannot miss a writer starting
                    // concurrently with HELLO, or complete before that writer.
                    writerTask = Task.Run(WriteLoop);
                }
            }
            return 0;
        }
        catch (Exception ex) { Diagnostics.Fault("client-receive " + session.Describe(), ex); Console.Error.WriteLine(ex.Message); Stop(); return Marshal.GetHRForException(ex); }
    }
    private async Task WriteLoop()
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var encoded = new byte[Protocol.MaxEncodedFrame];
            pointer = Marshal.AllocHGlobal(1200);
            while (!session.Stop.IsCancellationRequested)
            {
                session.Tick();
                // Bound each batch so heartbeat and cancellation cannot starve under load.
                for (int n = 0; n < 32 && session.TryTake(out var frame); n++)
                {
                    int length = Protocol.Encode(frame, encoded);
                    for (int offset = 0; offset < length; offset += 1200)
                    {
                        // Check between chunks without interleaving another frame.
                        session.Stop.Token.ThrowIfCancellationRequested();
                        session.Tick();
                        session.Stop.Token.ThrowIfCancellationRequested();
                        int count = Math.Min(1200, length - offset);
                        Marshal.Copy(encoded, offset, pointer, count);
                        session.NativeWriteStarted();
                        if (offset == 0) session.Metrics.RecordQueueDelay(frame.QueuedAt);
                        long writeStarted = session.Metrics.Timestamp();
                        try
                        {
                            Marshal.ThrowExceptionForHR(channel.Write((uint)count, pointer, IntPtr.Zero));
                        }
                        // This measures the COM call, not remote delivery.
                        finally { session.Metrics.RecordWriteDuration(writeStarted); }
                        session.NativeWriteCompleted(count);
                    }
                    frame.Written?.Invoke();
                }
                // A remaining batch may have consumed the wake signal; poll once per second at worst.
                if (!session.HasPending) await session.WaitAsync(session.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostics.Fault("client-write-loop " + session.Describe(), ex); Console.Error.WriteLine($"DVC write: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            // Stop may run on a callback while Write is blocked. Only the writer
            // releases this memory, after its final native call has returned.
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
            Stop();
        }
    }
    public int OnClose() { Diagnostics.Write("client-native-onclose " + session.Describe()); Stop(); return 0; }
    public void Stop() => Stop(helloExpired: false);

    private void Stop(bool helloExpired)
    {
        Task writer;
        lock (receiveGate)
        {
            if (Closed || helloExpired && writerTask != null) return;
            Volatile.Write(ref closed, 1);
            writer = writerTask ?? Task.CompletedTask;
        }
        if (helloExpired) Diagnostics.Write("client-hello-timeout " + session.Describe());
        handshakeDeadline.Dispose();
        try { session.Dispose(); }
        catch (Exception ex) { Diagnostics.Fault("client-session-stop " + session.Describe(), ex); }
        try { onClosed(this); }
        catch (Exception ex) { Diagnostics.Fault("client-channel-detach " + session.Describe(), ex); }

        // Exactly one closer is started, even if OnClose/timeout/Exit race. It is
        // independent of the writer so Close can unblock an in-flight COM Write.
        // Neither native call runs under receiveGate or a plugin/process lock.
        Task close = Task.Factory.StartNew(() =>
        {
            try { Marshal.ThrowExceptionForHR(channel.Close()); }
            catch (Exception ex) { Diagnostics.Fault("client-native-close " + session.Describe(), ex); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = CompleteShutdownAsync(writer, close);
    }

    private async Task CompleteShutdownAsync(Task writer, Task close)
    {
        try
        {
            await Task.WhenAll(writer, close).ConfigureAwait(false);
            await handshakeDeadline.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Diagnostics.Fault("client-channel-drain " + session.Describe(), ex); }
        finally
        {
            completion.TrySetResult();
            onDrained(this);
        }
    }
}
