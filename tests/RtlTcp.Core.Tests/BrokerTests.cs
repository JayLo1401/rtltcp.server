using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RtlTcp.Core.Remote.Broker;

namespace RtlTcp.Core.Tests;

public sealed class BrokerTests
{
    private static readonly Guid Session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Rendezvous_query_layout_matches_protocol_md()
    {
        var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var p = Packets.RendezvousQuery(Session, nonce, Key);
        Assert.Equal(64, p.Length);
        Assert.Equal("ROI1"u8.ToArray(), p[..4]);
        Assert.Equal(1, p[4]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), p[5..21]);  // big-endian UUID
        Assert.Equal(nonce, p[21..29]);
        Assert.All(p[29..48], b => Assert.Equal(0, b));
        Assert.Equal(HMACSHA256.HashData(Key, p[..48])[..16], p[48..]);
        var parsed = Packets.ParseRendezvousQuery(p, _ => Key)!.Value;
        Assert.Equal(Session, parsed.Session);
        Assert.Equal(nonce, parsed.Nonce);
    }

    [Theory]
    [InlineData("84.115.1.2", 40211, 4)]
    [InlineData("2a02:8388::1", 51234, 6)]
    public void Rendezvous_answer_roundtrip_with_mac_check(string ip, int port, byte family)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        var p = Packets.RendezvousAnswer(Session, nonce, new IPEndPoint(IPAddress.Parse(ip), port), Key);
        Assert.Equal(64, p.Length);
        Assert.Equal(2, p[4]);
        Assert.Equal(family, p[29]);
        Assert.Equal(port, p[30] << 8 | p[31]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse(ip), port), Packets.ParseRendezvousAnswer(p, Session, nonce, Key));

        p[40] ^= 1; // tampered address
        Assert.Null(Packets.ParseRendezvousAnswer(p, Session, nonce, Key));
    }

    [Fact]
    public void Punch_packet_is_45_bytes_and_rejects_other_sessions()
    {
        var p = Packets.Punch(Packets.PunchProbe, Session, Key);
        Assert.Equal(45, p.Length);
        Assert.Equal(HMACSHA256.HashData(Key, p[..29])[..16], p[29..]);
        Assert.Equal(Packets.PunchProbe, Packets.ParsePunch(p, Session, Key));
        Assert.Equal(0, Packets.ParsePunch(p, Guid.NewGuid(), Key));
        Assert.Equal(0, Packets.ParsePunch(p, Session, RandomNumberGenerator.GetBytes(32)));
    }

    [Theory]
    [InlineData("[2a02:8388::1]:51234", true)]
    [InlineData("192.168.1.5:51234", true)]
    [InlineData("mediator.example.com:3478", false)]
    [InlineData("2a02:8388::1:51234", false)]
    public void Candidates_are_ip_literals_only(string text, bool valid) =>
        Assert.Equal(valid, SessionSocket.Parse(text) is not null);

    [Fact]
    public async Task Gather_gets_reflexive_address_from_rendezvous_and_detects_no_nat_on_loopback()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rvPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var r = await server.ReceiveAsync();
                if (Packets.ParseRendezvousQuery(r.Buffer, _ => Key) is { } q)
                    await server.SendAsync(Packets.RendezvousAnswer(q.Session, q.Nonce, r.RemoteEndPoint, Key), r.RemoteEndPoint);
            }
        });

        using var s = SessionSocket.Bind();
        var g = await s.GatherAsync(Session, Key, [("127.0.0.1", rvPort)], TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, s.LocalPort), g.ReflexiveV4);
        Assert.Contains($"127.0.0.1:{s.LocalPort}", g.Candidates);
    }

    [Fact]
    public async Task Two_sockets_punch_each_other_and_agree_on_the_path()
    {
        using var a = SessionSocket.Bind();
        using var b = SessionSocket.Bind();
        var start = DateTimeOffset.UtcNow.AddMilliseconds(100);
        IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);
        var dead = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9); // TEST-NET, never answers

        var ta = a.PunchAsync(Session, Key, [dead, Ep(b.LocalPort)], start, CancellationToken.None);
        var tb = b.PunchAsync(Session, Key, [Ep(a.LocalPort)], start, CancellationToken.None);
        Assert.Equal(Ep(b.LocalPort), await ta);
        Assert.Equal(Ep(a.LocalPort), await tb);
    }

    [Fact]
    public async Task Punch_with_wrong_key_finds_no_path()
    {
        using var a = SessionSocket.Bind();
        using var b = SessionSocket.Bind();
        var start = DateTimeOffset.UtcNow;
        var ta = a.PunchAsync(Session, Key, [new IPEndPoint(IPAddress.Loopback, b.LocalPort)], start, CancellationToken.None);
        var tb = b.PunchAsync(Session, RandomNumberGenerator.GetBytes(32), [new IPEndPoint(IPAddress.Loopback, a.LocalPort)], start,
            CancellationToken.None);
        Assert.Null(await ta);
        Assert.Null(await tb);
    }
}
