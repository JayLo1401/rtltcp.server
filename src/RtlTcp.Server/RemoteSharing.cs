using System.Net;
using System.Net.Quic;
using RtlTcp.Core.Auth;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Remote;
using RtlTcp.Core.Usb;

// QUIC types are only created after QuicConnection/QuicListener.IsSupported was checked (Supported, _identity).
#pragma warning disable CA1416

namespace RtlTcp.Server;

/// <summary>
/// Host side of sharing sticks between sites: own certificate (data dir, remote/identity.pfx),
/// the QUIC listener for peers (Standort 1) and the sources for remote sticks (Standort 2).
/// Needs QUIC (Windows 11 / Server 2022, Linux with libmsquic); without it remote features report why.
/// </summary>
public sealed class RemoteSharing(string dataDir, ILogger log) : IDisposable
{
    private PeerIdentity? _identity;
    private ShareHost? _host;

    public bool Supported { get; } = QuicConnection.IsSupported && QuicListener.IsSupported;

    /// <summary>Why remote sharing is not available; null if it is.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>SHA-256 of our certificate; peers enter it to trust this server.</summary>
    public string? Fingerprint => _identity?.Fingerprint;

    /// <summary>UDP endpoint the listener is bound to, null if not listening.</summary>
    public IPEndPoint? ListenEndPoint => _host?.LocalEndPoint;

    /// <summary>Error of the last listener start (e.g. port in use).</summary>
    public string? ListenError { get; private set; }

    public void Init()
    {
        if (!QuicConnection.IsSupported || !QuicListener.IsSupported)
        {
            Unavailable = OperatingSystem.IsLinux()
                ? "QUIC nicht verfügbar: Paket libmsquic fehlt"
                : "QUIC nicht verfügbar (Windows 11 bzw. Server 2022 nötig)";
            log.LogWarning("Remote sharing disabled: {Reason}", Unavailable);
            return;
        }
        try
        {
            _identity = PeerIdentity.LoadOrCreate(Path.Combine(dataDir, "remote", "identity.pfx"),
                $"rtltcp.server {Environment.MachineName}");
            log.LogInformation("Remote sharing: own certificate fingerprint {Fingerprint}", _identity.Fingerprint);
        }
        catch (Exception ex)
        {
            Unavailable = $"Zertifikat nicht verfügbar: {ex.Message}";
            log.LogError(ex, "Remote sharing: cannot load or create the certificate");
        }
    }

    public ISdrDeviceSource SourceFor(RemoteDeviceConfig remote, ILogger deviceLog) =>
        Supported && _identity is not null
            ? new RemoteDeviceSource(remote, _identity, deviceLog)
            : new UnavailableSource(Unavailable ?? "QUIC nicht verfügbar");

    public async Task StartShareHostAsync(RemoteConfig cfg, IStreamUserStore users, string version,
        Func<string, DeviceManager?> findDevice)
    {
        ListenError = null;
        if (cfg.ListenPort <= 0 || !Supported || _identity is null) return;
        try
        {
            var bind = IPAddress.TryParse(cfg.Bind, out var a) ? a : IPAddress.IPv6Any;
            var host = new ShareHost(new IPEndPoint(bind, cfg.ListenPort), _identity, () => cfg.Peers, findDevice,
                users, version, log);
            await host.StartAsync();
            _host = host;
        }
        catch (Exception ex) when (ex is QuicException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            ListenError = ex.Message;
            log.LogError("Remote sharing: cannot listen on udp {Bind}:{Port}: {Error}", cfg.Bind, cfg.ListenPort, ex.Message);
        }
    }

    public async Task StopShareHostAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null) await host.DisposeAsync();
    }

    public void Dispose() => _identity?.Dispose();

    /// <summary>Remote stick configured, but this machine cannot do QUIC.</summary>
    private sealed class UnavailableSource(string reason) : ISdrDeviceSource
    {
        public bool IsRemote => true;
        public string? LastError => reason;
        public ISdrDevice? TryOpen(string serial, IReadOnlyCollection<string> exclude) => null;
        public IReadOnlyList<UsbDeviceInfo> List() => [];
    }
}
