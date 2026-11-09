using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Server;

namespace RtlTcp.Core.Remote;

/// <summary>
/// Provider side (Standort 1): accepts QUIC connections from other rtltcp.server sites whose certificate
/// fingerprint is a known peer, and serves each stream like a local v2 client of the requested stick
/// (same ownership rule, idle shutdown and reconnect).
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ShareHost : IAsyncDisposable
{
    private static readonly TimeSpan PreambleTimeout = TimeSpan.FromSeconds(10);

    private readonly IPEndPoint _endPoint;
    private readonly PeerIdentity _identity;
    private readonly Func<X509Certificate, PeerGrant?> _authorize;
    private readonly Func<string, DeviceManager?> _findDevice;
    private readonly string _version;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private QuicListener? _listener;
    private Task? _accept;

    /// <summary>A peer allowed by its certificate: display name and which sticks (serials) it may use.</summary>
    public sealed record PeerGrant(string Name, Func<string, bool> MayUse);

    /// <summary>Manually configured peers (test mode), read per connection so changes apply immediately.</summary>
    public ShareHost(IPEndPoint endPoint, PeerIdentity identity, Func<IReadOnlyList<RemotePeerConfig>> peers,
        Func<string, DeviceManager?> findDevice, string version, ILogger log)
        : this(endPoint, identity, cert => peers().FirstOrDefault(p => RemoteProtocol.FingerprintMatches(cert, p.Fingerprint)) is { } p
            ? new PeerGrant(p.Name, s => p.Devices.Contains(s, StringComparer.OrdinalIgnoreCase)) : null,
            findDevice, version, log)
    {
    }

    /// <param name="authorize">Decides per peer certificate; null = refused in the TLS handshake.</param>
    /// <param name="findDevice">Running stick by serial (share id).</param>
    public ShareHost(IPEndPoint endPoint, PeerIdentity identity, Func<X509Certificate, PeerGrant?> authorize,
        Func<string, DeviceManager?> findDevice, string version, ILogger log)
    {
        _endPoint = endPoint;
        _identity = identity;
        _authorize = authorize;
        _findDevice = findDevice;
        _version = version;
        _log = log;
    }

    public IPEndPoint? LocalEndPoint => _listener?.LocalEndPoint;

    /// <summary>Called when a peer connection ends (broker sessions end then).</summary>
    public event Action? ConnectionClosed;

    /// <summary>Number of peer connections currently open.</summary>
    public int Connections => Volatile.Read(ref _connections);

    private int _connections;

    public async Task StartAsync()
    {
        _listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = _endPoint,
            ApplicationProtocols = [RemoteProtocol.Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = RemoteProtocol.ErrorStreamAborted,
                DefaultCloseErrorCode = RemoteProtocol.ErrorNone,
                IdleTimeout = RemoteProtocol.IdleTimeout,
                KeepAliveInterval = RemoteProtocol.KeepAlive,
                MaxInboundBidirectionalStreams = 8,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [RemoteProtocol.Alpn],
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && _authorize(cert) is not null,
                },
            }),
        }, _cts.Token);
        _log.LogInformation("Remote sharing: listening on udp {EndPoint}, certificate {Fingerprint}",
            _listener.LocalEndPoint, _identity.Fingerprint);
        _accept = AcceptLoopAsync(_cts.Token);
    }

    private void Track(Task task)
    {
        _tasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => _tasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicConnection conn;
            try
            {
                conn = await _listener!.AcceptConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is QuicException or System.Security.Authentication.AuthenticationException
                                           or OperationCanceledException)
            {
                // failed handshake, e.g. unknown certificate: the peer gets nothing usable
                _log.LogInformation("Remote sharing: connection refused: {Error}", ex.Message);
                continue;
            }
            Track(Task.Run(() => ServeConnectionAsync(conn, ct), CancellationToken.None));
        }
    }

    private async Task ServeConnectionAsync(QuicConnection conn, CancellationToken ct)
    {
        await using var _ = conn;
        var peer = conn.RemoteCertificate is { } cert ? _authorize(cert) : null;
        var ip = conn.RemoteEndPoint.Address;
        var ipText = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        if (peer is null)
        {
            await conn.CloseAsync(RemoteProtocol.ErrorNone, CancellationToken.None);
            return;
        }
        var who = $"{peer.Name} ({ipText})";
        Interlocked.Increment(ref _connections);
        _log.LogInformation("Remote sharing: {Peer} connected", who);
        var streams = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await conn.AcceptInboundStreamAsync(ct);
                streams.Add(Task.Run(() => ServeStreamAsync(stream, peer, ip, who, ct), CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is QuicException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            await Task.WhenAll(streams);
            Interlocked.Decrement(ref _connections);
            _log.LogInformation("Remote sharing: {Peer} disconnected", who);
            ConnectionClosed?.Invoke();
        }
    }

    private async Task ServeStreamAsync(QuicStream stream, PeerGrant peer, IPAddress ip, string who, CancellationToken ct)
    {
        await using var _ = stream;
        try
        {
            string share;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(PreambleTimeout);
                share = await RemoteProtocol.ReadSharePreambleAsync(stream, t.Token);
            }

            var manager = peer.MayUse(share) ? _findDevice(share) : null;
            if (manager is null)
            {
                _log.LogInformation("Remote sharing: {Peer} asked for stick {Share}, which is not shared with it", who, share);
                await RejectAsync(stream, $"Stick {share} is not shared with this server", ct);
                return;
            }

            var conn = new ClientConnection(stream, ip, who, stream.CompleteWrites, waitForHello: false);
            await new ClientSession(conn, manager, _version, _log, ct).RunAsync();
        }
        catch (Exception ex) when (ex is QuicException or IOException or OperationCanceledException
                                       or EndOfStreamException or ProtocolException) { }
    }

    /// <summary>Answers like a v2 server with a fatal NOT_FOUND, so the other side can show why.</summary>
    private static async Task RejectAsync(QuicStream stream, string message, CancellationToken ct)
    {
        var hello = new byte[Command.Size];
        await stream.ReadExactlyAsync(hello, ct);
        var header = new byte[V2.HeaderSize];
        V2.WriteHeader(header, V2HeaderFlags.None);
        var json = V2Json.Serialize(new ErrorMessage(ErrorCodes.NotFound, message, true));
        var frame = new byte[FrameHeader.Size + json.Length];
        new FrameHeader(FrameType.Json, json.Length).Write(frame);
        json.CopyTo(frame, FrameHeader.Size);
        await stream.WriteAsync(header.Concat(frame).ToArray(), completeWrites: true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_tasks.Keys);
        if (_listener is not null) await _listener.DisposeAsync();
        _cts.Dispose();
    }
}
