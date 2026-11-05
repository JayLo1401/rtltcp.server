using System.Buffers.Binary;

namespace RtlTcp.Core.Protocol;

/// <summary>Command codes of the classic rtl_tcp protocol (v1). See docs/protocol-v2.md §1.</summary>
public enum RtlCommand : byte
{
    SetFrequency = 0x01,
    SetSampleRate = 0x02,
    SetGainMode = 0x03,
    SetGain = 0x04,
    SetFreqCorrection = 0x05,
    SetIfGain = 0x06,
    SetTestMode = 0x07,
    SetAgcMode = 0x08,
    SetDirectSampling = 0x09,
    SetOffsetTuning = 0x0A,
    SetRtlXtal = 0x0B,
    SetTunerXtal = 0x0C,
    SetGainByIndex = 0x0D,
    SetBiasTee = 0x0E,

    /// <summary>rtltcp_v2 negotiation, see <see cref="V2.HelloMagic"/>.</summary>
    HelloV2 = 0xF2,
}

/// <summary>Tuner numbering as used in the v1 header.</summary>
public enum TunerType : uint
{
    Unknown = 0,
    E4000 = 1,
    Fc0012 = 2,
    Fc0013 = 3,
    Fc2580 = 4,
    R820T = 5,
    R828D = 6,
}

/// <summary>A 5-byte v1 command: u8 code | u32 param (big-endian).</summary>
public readonly record struct Command(RtlCommand Code, uint Param)
{
    public const int Size = 5;

    /// <summary>Parameter interpreted as signed (e.g. ppm correction).</summary>
    public int SignedParam => unchecked((int)Param);

    public static Command Read(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size) throw new ArgumentException("Command needs 5 bytes", nameof(src));
        return new Command((RtlCommand)src[0], BinaryPrimitives.ReadUInt32BigEndian(src[1..]));
    }

    public void Write(Span<byte> dst)
    {
        dst[0] = (byte)Code;
        BinaryPrimitives.WriteUInt32BigEndian(dst[1..], Param);
    }

    public byte[] ToArray()
    {
        var b = new byte[Size];
        Write(b);
        return b;
    }
}

public static class V1
{
    public const int HeaderSize = 12;
    public static ReadOnlySpan<byte> Magic => "RTL0"u8;

    /// <summary>Writes the 12-byte header: "RTL0" | u32 tuner type | u32 gain count.</summary>
    public static void WriteHeader(Span<byte> dst, TunerType tuner, uint gainCount)
    {
        Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], (uint)tuner);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], gainCount);
    }
}
