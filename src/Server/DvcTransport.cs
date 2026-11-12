using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpProxy.Server;

internal static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WTSVirtualChannelOpenEx(uint session, string name, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSVirtualChannelClose(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSVirtualChannelQuery(IntPtr channel, int query, out IntPtr buffer, out uint bytes);
    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr file, IntPtr buffer, uint size, IntPtr count, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, IntPtr buffer, uint size, IntPtr count, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint count, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
}

internal sealed class DvcTransport(ProxyMode mode = ProxyMode.ServerProxy)
{
    private Session? current;
    public Session? Current => Volatile.Read(ref current);
    public void Run(uint sessionId, CancellationToken ct)
    {
        Console.WriteLine("Server transport v9: persistent overlapped full-frame writes and queued-output wakeups.");
        while (!ct.IsCancellationRequested)
        {
            IntPtr handle = IntPtr.Zero;
            using var session = new Session(false, mode);
            try
            {
                handle = Native.WTSVirtualChannelOpenEx(sessionId, Protocol.ChannelName, 1);
                if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open DVC");
                IntPtr file = QueryFile(handle);
                Volatile.Write(ref current, session);
                Pump(session, file, ct);
            }
            catch (Exception ex)
            {
                string code = ex is Win32Exception native ? $" Win32={native.NativeErrorCode}" : "";
                Diagnostics.Fault("server-dvc " + session.Describe() + " " + SessionState(sessionId), ex);
                Console.Error.WriteLine($"DVC: {ex.Message}{code}; retry in 3 seconds.");
            }
            finally
            {
                Volatile.Write(ref current, null);
                session.Dispose();
                // Pump has cancelled/drained both native requests. Only WTS closes
                // the borrowed file handle returned by QueryFile.
                if (handle != IntPtr.Zero) Native.WTSVirtualChannelClose(handle);
            }
            ct.WaitHandle.WaitOne(3000);
        }
    }

    private static string SessionState(uint sessionId)
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, 8, out IntPtr buffer, out uint bytes))
            return $"wtsStateQueryError={Marshal.GetLastWin32Error()}";
        try { return bytes >= 4 ? $"wtsConnectState={Marshal.ReadInt32(buffer)}" : "wtsStateTruncated"; }
        finally { Native.WTSFreeMemory(buffer); }
    }

    private static IntPtr QueryFile(IntPtr channel)
    {
        if (!Native.WTSVirtualChannelQuery(channel, 1, out IntPtr buffer, out uint size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSVirtualChannelQuery failed");
        try
        {
            if (size != IntPtr.Size) throw new IOException("Invalid WTS file-handle size.");
            IntPtr file = Marshal.ReadIntPtr(buffer);
            if (file == IntPtr.Zero || file == new IntPtr(-1)) throw new IOException("Invalid WTS file handle.");
            return file;
        }
        finally { Native.WTSFreeMemory(buffer); }
    }

    private static void Pump(Session session, IntPtr file, CancellationToken ct)
    {
        using var read = new OverlappedIo(file, 1608); // CHANNEL_PDU_LENGTH
        // The server file handle accepts a complete application frame. The client
        // IWTSVirtualChannel writer has a separate <=1200-byte native limit.
        using var write = new OverlappedIo(file, Protocol.MaxEncodedFrame);
        var nativeRecords = new NativeRecords();
        Frame? outgoing = null;
        var encoded = new byte[Protocol.MaxEncodedFrame];
        int requested = 0;
        long writeStarted = 0;
        long writeTimingStarted = 0;
        WaitHandle[] idleWaits = { read.Completed, ct.WaitHandle, session.Stop.Token.WaitHandle, session.OutputReady };
        WaitHandle[] writingWaits = { read.Completed, ct.WaitHandle, session.Stop.Token.WaitHandle, write.Completed };
        read.BeginRead();
        while (!ct.IsCancellationRequested && !session.Stop.IsCancellationRequested)
        {
            session.Tick();
            bool progress = false;
            // Drain exactly one completed read and immediately post its successor.
            // An incomplete read is retained, never discarded on a poll deadline.
            if (read.TryComplete(out int received))
            {
                if (received == 0) throw new IOException("DVC reached EOF.");
                session.NativeRead(received);
                nativeRecords.Push(read.CopyRead(received), session);
                read.BeginRead();
                progress = true;
            }
            if (write.TryComplete(out int written))
            {
                session.Metrics.RecordWriteDuration(writeTimingStarted);
                if (written != requested) throw new IOException("Partial DVC write; closing channel.");
                session.NativeWriteCompleted(written);
                outgoing!.Written?.Invoke();
                outgoing = null;
                progress = true;
            }
            if (!write.Pending)
            {
                // Consume before scanning: an enqueue racing with the scan leaves
                // a signal for WaitAny. Never wait on that signal during a write.
                session.ConsumeOutputSignal();
                if (session.TryTake(out var frame))
                {
                    outgoing = frame;
                    requested = Protocol.Encode(frame, encoded);
                    writeStarted = Environment.TickCount64;
                    session.NativeWriteStarted();
                    session.Metrics.RecordQueueDelay(frame.QueuedAt);
                    writeTimingStarted = session.Metrics.Timestamp();
                    write.BeginWrite(encoded, 0, requested);
                    progress = true;
                }
            }
            if (write.Pending && Environment.TickCount64 - writeStarted > 15000)
                throw new IOException("Native DVC write stalled for 15 seconds.");
            if (progress) continue;
            // The timeout services Tick; queued output wakes an idle writer at once.
            // Polling never cancels or restarts an incomplete ReadFile.
            WaitHandle.WaitAny(write.Pending ? writingWaits : idleWaits, 20);
        }
    }
}

// Our client sends native writes <=1200 bytes. Validate native framing before
// exposing any data to the application parser. Reassemble FIRST/LAST fragments.
internal sealed class NativeRecords
{
    private readonly byte[] record = new byte[1200];
    private int expected;
    private int used;

    public void Push(ReadOnlySpan<byte> bytes, Session session)
    {
        if (bytes.Length < 8) throw new IOException("Truncated native DVC header.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        int count = bytes.Length - 8;
        if (length is < 1 or > 1200) throw new IOException($"Invalid native DVC length={length}; chunk={count}; flags=0x{flags:X}.");
        if ((flags & 1) != 0)
        {
            if (expected != 0) throw new IOException("Native DVC FIRST before previous LAST.");
            expected = (int)length;
            used = 0;
        }
        if (expected == 0 || length != expected || count > expected - used)
            throw new IOException("Invalid native DVC fragment sequence.");
        bytes[8..].CopyTo(record.AsSpan(used));
        used += count;
        if ((flags & 2) != 0)
        {
            if (used != expected) throw new IOException($"Truncated native DVC record: {used}/{expected}.");
            session.Receive(record.AsSpan(0, expected));
            expected = 0;
            used = 0;
        }
    }
}
