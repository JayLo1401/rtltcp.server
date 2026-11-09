using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace RtlTcp.Core.Remote.Broker;

/// <summary>
/// UDP packets of the connection setup (rtl.over.internet docs/protocol.md §3, §4):
/// "ROI1" rendezvous request/response (64 bytes, MAC at 48) and "ROIP" punch probe/ack (45 bytes, MAC at 29).
/// MAC = HMAC-SHA256(punch_key, preceding bytes) truncated to 16 bytes. UUIDs are big-endian (RFC 4122).
/// </summary>
public static class Packets
{
    public const int RendezvousSize = 64;
    public const int PunchSize = 45;
    private const int MacSize = 16;
    private const int RendezvousMacAt = 48;
    private const int PunchMacAt = 29;

    public const byte RendezvousRequest = 1;
    public const byte RendezvousResponse = 2;
    public const byte PunchProbe = 1;
    public const byte PunchAck = 2;

    private static ReadOnlySpan<byte> Roi1 => "ROI1"u8;
    private static ReadOnlySpan<byte> Roip => "ROIP"u8;

    private static void WriteMac(Span<byte> packet, int macAt, byte[] key)
    {
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(key, packet[..macAt], full);
        full[..MacSize].CopyTo(packet[macAt..]);
    }

    private static bool CheckMac(ReadOnlySpan<byte> packet, int macAt, byte[] key)
    {
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(key, packet[..macAt], full);
        return CryptographicOperations.FixedTimeEquals(full[..MacSize], packet.Slice(macAt, MacSize));
    }

    private static void WriteSession(Span<byte> dst, Guid session)
    {
        if (!session.TryWriteBytes(dst, bigEndian: true, out _)) throw new ArgumentException("buffer too small");
    }

    // ---------------------------------------------------------------- rendezvous

    public static byte[] RendezvousQuery(Guid session, ReadOnlySpan<byte> nonce, byte[] key)
    {
        var p = new byte[RendezvousSize];
        Roi1.CopyTo(p);
        p[4] = RendezvousRequest;
        WriteSession(p.AsSpan(5), session);
        nonce[..8].CopyTo(p.AsSpan(21));
        WriteMac(p, RendezvousMacAt, key);
        return p;
    }

    /// <summary>Server side of §3 (used by tests as a stand-in for rtl.over.internet).</summary>
    public static byte[] RendezvousAnswer(Guid session, ReadOnlySpan<byte> nonce, IPEndPoint reflexive, byte[] key)
    {
        var p = new byte[RendezvousSize];
        Roi1.CopyTo(p);
        p[4] = RendezvousResponse;
        WriteSession(p.AsSpan(5), session);
        nonce[..8].CopyTo(p.AsSpan(21));
        var addr = reflexive.Address;
        p[29] = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || addr.IsIPv4MappedToIPv6 ? (byte)4 : (byte)6;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(30), (ushort)reflexive.Port);
        (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? addr.MapToIPv6() : addr).TryWriteBytes(p.AsSpan(32, 16), out _);
        WriteMac(p, RendezvousMacAt, key);
        return p;
    }

    /// <summary>Validated reflexive address from a rendezvous response, or null (wrong session, nonce or MAC).</summary>
    public static IPEndPoint? ParseRendezvousAnswer(ReadOnlySpan<byte> p, Guid session, ReadOnlySpan<byte> nonce, byte[] key)
    {
        if (p.Length != RendezvousSize || !p[..4].SequenceEqual(Roi1) || p[4] != RendezvousResponse) return null;
        Span<byte> s = stackalloc byte[16];
        WriteSession(s, session);
        if (!p.Slice(5, 16).SequenceEqual(s) || !p.Slice(21, 8).SequenceEqual(nonce[..8])) return null;
        if (!CheckMac(p, RendezvousMacAt, key)) return null;
        var addr = new IPAddress(p.Slice(32, 16));
        if (p[29] == 4) addr = addr.MapToIPv4();
        return new IPEndPoint(addr, BinaryPrimitives.ReadUInt16BigEndian(p[30..]));
    }

    /// <summary>Parses a request (test stand-in for the rendezvous server). Returns session and nonce if the MAC is valid.</summary>
    public static (Guid Session, byte[] Nonce)? ParseRendezvousQuery(ReadOnlySpan<byte> p, Func<Guid, byte[]?> keyOf)
    {
        if (p.Length != RendezvousSize || !p[..4].SequenceEqual(Roi1) || p[4] != RendezvousRequest) return null;
        var session = new Guid(p.Slice(5, 16), bigEndian: true);
        if (keyOf(session) is not { } key || !CheckMac(p, RendezvousMacAt, key)) return null;
        return (session, p.Slice(21, 8).ToArray());
    }

    // ---------------------------------------------------------------- punch

    public static byte[] Punch(byte type, Guid session, byte[] key)
    {
        var p = new byte[PunchSize];
        Roip.CopyTo(p);
        p[4] = type;
        WriteSession(p.AsSpan(5), session);
        RandomNumberGenerator.Fill(p.AsSpan(21, 8));
        WriteMac(p, PunchMacAt, key);
        return p;
    }

    /// <summary>Type of a valid punch packet of this session (probe/ack), or 0.</summary>
    public static byte ParsePunch(ReadOnlySpan<byte> p, Guid session, byte[] key)
    {
        if (p.Length != PunchSize || !p[..4].SequenceEqual(Roip) || p[4] is not (PunchProbe or PunchAck)) return 0;
        Span<byte> s = stackalloc byte[16];
        WriteSession(s, session);
        if (!p.Slice(5, 16).SequenceEqual(s) || !CheckMac(p, PunchMacAt, key)) return 0;
        return p[4];
    }
}
