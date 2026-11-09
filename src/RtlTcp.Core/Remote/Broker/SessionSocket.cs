using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RtlTcp.Core.Remote.Broker;

/// <summary>Reflexive address of this site as seen by the rendezvous endpoint, and the IPv4 NAT behaviour.</summary>
public static class NatTypes
{
    public const string None = "none";
    public const string EndpointIndependent = "endpoint_independent";
    public const string Symmetric = "symmetric";
    public const string Unknown = "unknown";
}

/// <summary>
/// The UDP socket of one session (docs/protocol.md §3, §4): gathers candidates, asks the rendezvous endpoint for
/// the reflexive address, punches the hole to the peer. Afterwards it is closed and QUIC binds the same local port,
/// so the NAT mapping just created is reused.
/// </summary>
public sealed class SessionSocket : IDisposable
{
    public const int MaxCandidates = 16;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PunchDuration = TimeSpan.FromSeconds(5);

    /// <summary>After the first ack: keep answering probes this long so the peer gets its ack too.</summary>
    private static readonly TimeSpan AnswerAfterWin = TimeSpan.FromMilliseconds(800);

    /// <summary>A winning IPv4 path waits this long for an IPv6 path (preferred).</summary>
    private static readonly TimeSpan PreferIPv6 = TimeSpan.FromMilliseconds(150);

    private readonly Socket _socket;

    private SessionSocket(Socket socket)
    {
        _socket = socket;
        LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public int LocalPort { get; }

    /// <summary>Binds a dual-stack UDP socket on a free port (or <paramref name="port"/>).</summary>
    public static SessionSocket Bind(int port = 0)
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        if (OperatingSystem.IsWindows())
        {
            // Without this, an ICMP "port unreachable" from a dead candidate makes the next receive throw.
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            s.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return new SessionSocket(s);
    }

    public static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    public static string Format(IPEndPoint ep)
    {
        ep = Normalize(ep);
        return ep.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ep.Address}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";
    }

    /// <summary>Parses "[v6]:port" / "a.b.c.d:port"; host names are not allowed.</summary>
    public static IPEndPoint? Parse(string candidate) =>
        IPEndPoint.TryParse(candidate, out var ep) && ep.Port > 0 && (candidate.StartsWith('[') || ep.AddressFamily == AddressFamily.InterNetwork)
            ? ep : null;

    // ---------------------------------------------------------------- candidates

    public sealed record Gathered(List<string> Candidates, string Nat, IPEndPoint? ReflexiveV4, IPEndPoint? ReflexiveV6);

    /// <summary>Local addresses + reflexive addresses from the rendezvous endpoints (both ports, IPv4 and IPv6).</summary>
    public async Task<Gathered> GatherAsync(Guid session, byte[] key, IReadOnlyList<(string Host, int Port)> rendezvous,
        TimeSpan timeout, CancellationToken ct)
    {
        var local = LocalAddresses();
        var (v4, v6) = await QueryRendezvousAsync(session, key, rendezvous, timeout, ct);

        var r4 = v4.Values.FirstOrDefault();
        var nat = r4 is null ? NatTypes.Unknown
            : local.Any(a => a.Equals(r4.Address)) ? NatTypes.None
            : v4.Count >= 2 && v4.Values.Select(e => e.ToString()).Distinct().Count() == 1 ? NatTypes.EndpointIndependent
            : v4.Count >= 2 ? NatTypes.Symmetric
            : NatTypes.Unknown;

        var list = new List<IPEndPoint>();
        // 1. public IPv6 (no NAT, the router firewall opens on simultaneous sending)
        list.AddRange(local.Where(IsGlobalV6).Select(a => new IPEndPoint(a, LocalPort)));
        if (v6.Values.FirstOrDefault() is { } r6) list.Add(r6);
        // 2. reflexive IPv4 (public side of the NAT), every distinct mapping
        list.AddRange(v4.Values);
        // 3. LAN (same site or VPN)
        list.AddRange(local.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => new IPEndPoint(a, LocalPort)));
        list.AddRange(local.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !IsGlobalV6(a) && !a.IsIPv6LinkLocal)
            .Select(a => new IPEndPoint(a, LocalPort)));

        var candidates = list.Select(Format).Distinct().Take(MaxCandidates).ToList();
        return new Gathered(candidates, nat, r4, v6.Values.FirstOrDefault());
    }

    private static bool IsGlobalV6(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal
        && !a.IsIPv6UniqueLocal && !IPAddress.IsLoopback(a) && (a.GetAddressBytes()[0] & 0xE0) == 0x20; // 2000::/3

    private static List<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                var a = u.Address;
                if (IPAddress.IsLoopback(a)) continue;
                if (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]) continue;
                if (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) result.Add(a);
            }
        }
        return result;
    }

    /// <summary>Sends a query to every address of every endpoint, repeats every 300 ms until all answered or timeout.</summary>
    private async Task<(Dictionary<int, IPEndPoint> V4, Dictionary<int, IPEndPoint> V6)> QueryRendezvousAsync(
        Guid session, byte[] key, IReadOnlyList<(string Host, int Port)> rendezvous, TimeSpan timeout, CancellationToken ct)
    {
        var v4 = new Dictionary<int, IPEndPoint>();
        var v6 = new Dictionary<int, IPEndPoint>();
        var targets = new List<(IPEndPoint Target, byte[] Nonce)>();
        foreach (var (host, port) in rendezvous)
        {
            IPAddress[] addrs;
            try { addrs = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException) { continue; }
            foreach (var a in addrs.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                targets.Add((new IPEndPoint(a, port), RandomNumberGenerator.GetBytes(8)));
        }
        if (targets.Count == 0) return (v4, v6);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var answered = new HashSet<int>();
        var receive = Task.Run(async () =>
        {
            var buf = new byte[256];
            while (answered.Count < targets.Count)
            {
                var r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cts.Token);
                var from = Normalize((IPEndPoint)r.RemoteEndPoint);
                for (var i = 0; i < targets.Count; i++)
                {
                    var (target, nonce) = targets[i];
                    if (!Normalize(target).Equals(from)) continue;
                    if (Packets.ParseRendezvousAnswer(buf.AsSpan(0, r.ReceivedBytes), session, nonce, key) is not { } refl) continue;
                    lock (answered)
                    {
                        if (!answered.Add(i)) continue;
                        (refl.AddressFamily == AddressFamily.InterNetwork ? v4 : v6).TryAdd(target.Port, refl);
                    }
                }
            }
        }, CancellationToken.None);

        try
        {
            while (!receive.IsCompleted)
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    lock (answered) if (answered.Contains(i)) continue;
                    var (target, nonce) = targets[i];
                    try { await _socket.SendToAsync(Packets.RendezvousQuery(session, nonce, key), SocketFlags.None, target, cts.Token); }
                    catch (SocketException) { } // e.g. no IPv6 route
                }
                await Task.WhenAny(receive, Task.Delay(300, cts.Token));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        try { await receive; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        lock (answered) return (v4, v6);
    }

    // ---------------------------------------------------------------- punch

    /// <summary>
    /// Sends probes to all peer candidates from <paramref name="startAt"/> on, answers the peer's probes with acks.
    /// Returns the peer endpoint whose ack arrived first (IPv6 preferred), or null after 5 s.
    /// </summary>
    public async Task<IPEndPoint?> PunchAsync(Guid session, byte[] key, IReadOnlyList<IPEndPoint> peer, DateTimeOffset startAt,
        CancellationToken ct)
    {
        var wait = startAt - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

        var probe = Packets.Punch(Packets.PunchProbe, session, key);
        var ack = Packets.Punch(Packets.PunchAck, session, key);
        IPEndPoint? winner = null;
        DateTimeOffset? wonAt = null;
        var started = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receive = Task.Run(async () =>
        {
            var buf = new byte[128];
            while (!cts.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cts.Token); }
                catch (SocketException) { continue; }
                var from = Normalize((IPEndPoint)r.RemoteEndPoint);
                switch (Packets.ParsePunch(buf.AsSpan(0, r.ReceivedBytes), session, key))
                {
                    case Packets.PunchProbe:
                        try { await _socket.SendToAsync(ack, SocketFlags.None, from, cts.Token); } catch (SocketException) { }
                        break;
                    case Packets.PunchAck:
                        lock (cts)
                        {
                            if (winner is null || (winner.AddressFamily == AddressFamily.InterNetwork && from.AddressFamily == AddressFamily.InterNetworkV6))
                            {
                                winner = from;
                                wonAt ??= DateTimeOffset.UtcNow;
                            }
                        }
                        break;
                }
            }
        }, CancellationToken.None);

        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                lock (cts)
                {
                    if (wonAt is { } w && (winner!.AddressFamily == AddressFamily.InterNetworkV6 ? now - w >= AnswerAfterWin
                                                                                                    : now - w >= AnswerAfterWin + PreferIPv6))
                        break;
                }
                if (now - started >= PunchDuration && wonAt is null) break;
                if (wonAt is null)
                    foreach (var p in peer)
                    {
                        try { await _socket.SendToAsync(probe, SocketFlags.None, p, cts.Token); }
                        catch (SocketException) { } // unreachable family / route
                    }
                await Task.Delay(ProbeInterval, cts.Token);
            }
        }
        finally
        {
            await cts.CancelAsync();
            try { await receive; } catch (OperationCanceledException) { }
        }
        ct.ThrowIfCancellationRequested();
        lock (cts) return winner;
    }

    public void Dispose() => _socket.Dispose();
}
