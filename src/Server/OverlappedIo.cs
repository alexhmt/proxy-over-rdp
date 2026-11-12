using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpProxy.Server;

// One persistent native request. Buffer and OVERLAPPED remain valid until completion,
// including across every poll timeout. The channel owns the borrowed file handle.
internal sealed class OverlappedIo : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr Event;
    }

    private readonly IntPtr file;
    private readonly IntPtr buffer;
    private readonly IntPtr overlapped;
    private readonly int capacity;
    private byte[]? readBuffer;
    private bool pending;
    private bool disposed;
    public EventWaitHandle Completed { get; } = new(false, EventResetMode.ManualReset);
    public bool Pending => pending;

    public OverlappedIo(IntPtr file, int capacity)
    {
        this.file = file;
        this.capacity = capacity;
        buffer = Marshal.AllocHGlobal(capacity);
        try { overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<State>()); }
        catch { Marshal.FreeHGlobal(buffer); Completed.Dispose(); throw; }
    }

    public void BeginRead() => Begin(null, 0, capacity);
    public void BeginWrite(byte[] bytes, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count), "Invalid native write range.");
        Begin(bytes, offset, count);
    }

    private void Begin(byte[]? bytes, int offset, int count)
    {
        if (disposed || pending) throw new InvalidOperationException("Native I/O request is not reusable yet.");
        if (count > capacity) throw new IOException("Native write too large.");
        Completed.Reset();
        Marshal.StructureToPtr(new State { Event = Completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
        if (bytes != null) Marshal.Copy(bytes, offset, buffer, count);
        bool ok = bytes == null
            ? Native.ReadFile(file, buffer, (uint)capacity, IntPtr.Zero, overlapped)
            : Native.WriteFile(file, buffer, (uint)count, IntPtr.Zero, overlapped);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        if (!ok && error != 997) throw new Win32Exception(error, bytes == null ? "ReadFile failed" : "WriteFile failed");
        pending = true;
        // Synchronous completion still goes through GetOverlappedResult. Explicitly
        // signal for that path; the request has already finished, so reuse is safe.
        if (ok) Completed.Set();
    }

    public bool TryComplete(out int count)
    {
        count = 0;
        if (!pending) return false;
        if (!Native.GetOverlappedResult(file, overlapped, out uint transferred, false))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 996) return false; // Keep the SAME request and buffer alive.
            pending = false;
            throw new Win32Exception(error, "Native DVC I/O failed");
        }
        pending = false;
        if (transferred > capacity) throw new IOException("Invalid native I/O count.");
        count = (int)transferred;
        return true;
    }

    public ReadOnlySpan<byte> CopyRead(int count)
    {
        if (disposed || pending) throw new InvalidOperationException("Native read has not completed.");
        if (count < 0 || count > capacity) throw new ArgumentOutOfRangeException(nameof(count));
        readBuffer ??= new byte[capacity];
        Marshal.Copy(buffer, readBuffer, 0, count);
        // The caller consumes this synchronously before the next CopyRead.
        return readBuffer.AsSpan(0, count);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pending)
        {
            // Cancel only this operation, then drain its terminal completion BEFORE
            // freeing its native memory or allowing WTSVirtualChannelClose.
            Native.CancelIoEx(file, overlapped);
            Native.GetOverlappedResult(file, overlapped, out _, true);
            pending = false;
        }
        Marshal.FreeHGlobal(overlapped);
        Marshal.FreeHGlobal(buffer);
        Completed.Dispose();
    }
}
