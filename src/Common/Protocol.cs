using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace RdpProxy;

internal enum Kind : byte
{
    Hello = 1, Open, OpenOk, OpenError, Data, Fin, Reset, WindowUpdate, Ping, Pong
}

internal sealed record Frame(Kind Kind, ulong StreamId, byte[] Body)
{
    public Action? Written { get; init; }
    public long QueuedAt { get; set; }
}

// uint32 length (excluding prefix), version, kind, uint64 stream ID; all integers big endian.
internal static class Protocol
{
    public const string ChannelName = "rdpproxy.v1";
    public const int BlockSize = 16384;
    public const int MaxEncodedFrame = BlockSize + 14;
    public const int Window = 262144;
    public const int MaxStreams = 64;
    public static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(Frame frame)
    {
        if (frame.Body.Length > BlockSize) throw new IOException("Frame too large.");
        var bytes = new byte[14 + frame.Body.Length];
        Encode(frame, bytes);
        return bytes;
    }

    public static int Encode(Frame frame, Span<byte> bytes)
    {
        if (frame.Body.Length > BlockSize) throw new IOException("Frame too large.");
        int length = 14 + frame.Body.Length;
        if (bytes.Length < length) throw new ArgumentException("Frame buffer too small.", nameof(bytes));
        BinaryPrimitives.WriteInt32BigEndian(bytes, length - 4);
        bytes[4] = 1;
        bytes[5] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt64BigEndian(bytes[6..], frame.StreamId);
        frame.Body.AsSpan().CopyTo(bytes[14..]);
        return length;
    }

    public static Frame Control(Kind kind, ulong id = 0) => new(kind, id, Array.Empty<byte>());
    public static byte[] Credit(int count)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, count);
        return bytes;
    }

    public static byte[] Target(string host, int port)
    {
        var name = Utf8.GetBytes(host);
        if (name.Length is < 1 or > 1024 || port is < 1 or > 65535)
            throw new IOException("Invalid target.");
        var bytes = new byte[name.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)port);
        name.CopyTo(bytes, 2);
        return bytes;
    }

    public static (string Host, int Port) ReadTarget(byte[] bytes)
    {
        if (bytes.Length is < 3 or > 1026) throw new IOException("Invalid target.");
        int port = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        string host = Utf8.GetString(bytes, 2, bytes.Length - 2);
        if (port == 0 || host.Any(char.IsControl)) throw new IOException("Invalid target.");
        return (host, port);
    }

    public static byte[] BoundAddress(IPEndPoint endpoint)
    {
        var address = endpoint.Address.GetAddressBytes();
        var bytes = new byte[address.Length + 3];
        bytes[0] = (byte)(address.Length == 4 ? 1 : 4);
        address.CopyTo(bytes, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(bytes.Length - 2), (ushort)endpoint.Port);
        return bytes;
    }
}

internal delegate void DataReceiver(ulong id, ReadOnlySpan<byte> data);

internal sealed class FrameDecoder
{
    private readonly byte[] buffer = new byte[Protocol.MaxEncodedFrame];
    private int used;
    private int expected = 4;

    public void Push(ReadOnlySpan<byte> bytes, Action<Frame> receiveControl, DataReceiver receiveData)
    {
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(expected - used, bytes.Length);
            bytes[..count].CopyTo(buffer.AsSpan(used));
            used += count;
            bytes = bytes[count..];
            if (used != expected) continue;
            if (expected == 4)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(buffer);
                if (length < 10 || length > Protocol.BlockSize + 10)
                    throw new IOException("Invalid frame length.");
                expected = length + 4;
                continue;
            }
            if (buffer[4] != 1 || !Enum.IsDefined((Kind)buffer[5]))
                throw new IOException("Unsupported protocol version or frame kind.");
            var kind = (Kind)buffer[5];
            ulong streamId = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(6));
            ReadOnlySpan<byte> body = buffer.AsSpan(14, expected - 14);
            used = 0;
            expected = 4;
            // DATA points into decoder storage and must be consumed before the callback returns.
            if (kind == Kind.Data) receiveData(streamId, body);
            else receiveControl(new Frame(kind, streamId, body.ToArray()));
        }
    }
}
