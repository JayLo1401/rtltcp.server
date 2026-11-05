using System.Buffers.Binary;

namespace RtlTcp.Core.Protocol;

/// <summary>Frame types of a v2 session (docs/protocol-v2.md §3.2).</summary>
public enum FrameType : byte
{
    Iq = 0x01,
    Json = 0x02,
    Command = 0x03,
}

public readonly record struct FrameHeader(FrameType Type, int Length)
{
    public const int Size = 5;

    public static FrameHeader Read(ReadOnlySpan<byte> src)
    {
        var len = BinaryPrimitives.ReadUInt32BigEndian(src[1..]);
        if (len > V2.MaxFrameLength)
            throw new ProtocolException($"Frame too large: {len} bytes");
        return new FrameHeader((FrameType)src[0], (int)len);
    }

    public void Write(Span<byte> dst)
    {
        dst[0] = (byte)Type;
        BinaryPrimitives.WriteUInt32BigEndian(dst[1..], (uint)Length);
    }
}

[Flags]
public enum V2HeaderFlags : ushort
{
    None = 0,
    AuthRequired = 1 << 0,
}

public static class V2
{
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 12;
    public const int MaxFrameLength = 16 * 1024 * 1024;

    /// <summary>Parameter of the HELLO command: ASCII "RTL2".</summary>
    public const uint HelloMagic = 0x52544C32;

    /// <summary>How long the server waits for HELLO before falling back to v1.</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromMilliseconds(200);

    public static ReadOnlySpan<byte> Magic => "RTL2"u8;

    public static Command Hello => new(RtlCommand.HelloV2, HelloMagic);

    public static bool IsHello(ReadOnlySpan<byte> first5) =>
        first5.Length >= Command.Size && Command.Read(first5) == Hello;

    /// <summary>"RTL2" | u16 version | u16 flags | u32 reserved.</summary>
    public static void WriteHeader(Span<byte> dst, V2HeaderFlags flags)
    {
        Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..], (ushort)flags);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], 0);
    }

    public static (ushort Version, V2HeaderFlags Flags) ReadHeader(ReadOnlySpan<byte> src)
    {
        if (!src[..4].SequenceEqual(Magic)) throw new ProtocolException("Not an RTL2 header");
        return (BinaryPrimitives.ReadUInt16BigEndian(src[4..]),
                (V2HeaderFlags)BinaryPrimitives.ReadUInt16BigEndian(src[6..]));
    }
}

public sealed class ProtocolException(string message) : Exception(message);
