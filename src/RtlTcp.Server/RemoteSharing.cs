using System.Net;
using System.Net.Quic;
using System.Text.Json;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Protocol;
using RtlTcp.Core.Remote;
using RtlTcp.Core.Remote.Broker;
using RtlTcp.Core.Usb;

namespace RtlTcp.Server;

/// <summary>
/// Host side of sharing sticks between sites: own certificate (data dir, remote/identity.pfx), the control
/// channels to rtl.over.internet (one per instance, tokens in remote/tokens.json), the manual QUIC listener
/// (test mode) and the sources for remote sticks. Needs QUIC (Windows 11 / Server 2022, Linux with libmsquic).
/// </summary>
public sealed class RemoteSharing(string dataDir, ILoggerFactory loggers) : IDisposable
{
    private readonly ILogger _log = loggers.CreateLogger("remote");
    private readonly string _dir = Path.Combine(dataDir, "remote");
    private readonly Lock _tokenLock = new();
    private PeerIdentity? _identity;
    private ShareHost? _host;
    private Dictionary<string, BrokerClient> _brokers = [];

    public bool Supported { get; } = QuicConnection.IsSupported && QuicListener.IsSupported;

    /// <summary>Why remote sharing is not available; null if it is.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>SHA-256 of our certificate; peers enter it to trust this server.</summary>
    public string? Fingerprint => _identity?.Fingerprint;

    /// <summary>UDP endpoint the manual listener is bound to, null if not listening.</summary>
    public IPEndPoint? ListenEndPoint => _host?.LocalEndPoint;

    /// <summary>Error of the last listener start (e.g. port in use).</summary>
    public string? ListenError { get; private set; }

    /// <summary>Stable id of this server towards rtl.over.internet (remote/server-id).</summary>
    public string ServerId { get; private set; } = "";

    public BrokerClient? Broker(string instanceId) => _brokers.GetValueOrDefault(instanceId);

    public void Init()
    {
        Directory.CreateDirectory(_dir);
        var idFile = Path.Combine(_dir, "server-id");
        if (!File.Exists(idFile)) File.WriteAllText(idFile, Guid.NewGuid().ToString());
        ServerId = File.ReadAllText(idFile).Trim();

        if (!Supported)
        {
            Unavailable = OperatingSystem.IsLinux()
                ? "QUIC nicht verfügbar: Paket libmsquic fehlt"
                : "QUIC nicht verfügbar (Windows 11 bzw. Server 2022 nötig)";
            _log.LogWarning("Remote sharing disabled: {Reason}", Unavailable);
            return;
        }
        try
        {
            _identity = PeerIdentity.LoadOrCreate(Path.Combine(_dir, "identity.pfx"), $"rtltcp.server {Environment.MachineName}");
            _log.LogInformation("Remote sharing: own certificate fingerprint {Fingerprint}", _identity.Fingerprint);
        }
        catch (Exception ex)
        {
            Unavailable = $"Zertifikat nicht verfügbar: {ex.Message}";
            _log.LogError(ex, "Remote sharing: cannot load or create the certificate");
        }
    }

    public static string ServerNameOf(RemoteConfig cfg) => cfg.ServerName.Trim() is { Length: > 0 } n ? n : Environment.MachineName;

    /// <summary>Instance and providing site of a remote stick (names as the broker reports them now).</summary>
    public RtlTcp.Core.Protocol.RemoteOrigin OriginOf(RemoteDeviceConfig remote)
    {
        if (remote.Instance.Length == 0) return new(null, remote.Host);
        var b = Broker(remote.Instance);
        var share = b?.Shares.FirstOrDefault(s => s.ShareId == remote.Share);
        return new(b?.InstanceName, share?.ProviderName);
    }

    /// <summary>Location/antenna of a remote stick as announced by its provider in the instance.</summary>
    public DeviceDetails? AnnouncedDetailsOf(RemoteDeviceConfig remote) =>
        remote.Instance.Length > 0 && Broker(remote.Instance)?.Shares.FirstOrDefault(s => s.ShareId == remote.Share) is { } s
            ? DeviceDetails.Of(s.Location, s.Antenna) : null;

    // ---------------------------------------------------------------- sources (user side)

    public ISdrDeviceSource SourceFor(RemoteDeviceConfig remote, ILogger deviceLog)
    {
        if (!Supported || _identity is null) return new UnavailableSource(Unavailable ?? "QUIC nicht verfügbar");
        IRemoteConnector connector = remote.Instance.Length > 0
            ? new BrokerConnector(() => Broker(remote.Instance), remote.Share)
            : new DirectConnector(remote, _identity);
        return new RemoteDeviceSource(connector, deviceLog);
    }

    // ---------------------------------------------------------------- start / stop

    public async Task StartAsync(ServerConfig cfg, string version, IReadOnlyList<DeviceManager> managers)
    {
        ListenError = null;
        if (!Supported || _identity is null) return;

        DeviceManager? Find(string serial) => managers.FirstOrDefault(m => m.Config.Remote is null &&
            (string.Equals(m.Config.Serial, serial, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(m.OpenSerial, serial, StringComparison.OrdinalIgnoreCase)));

        var r = cfg.Remote;
        if (r.ListenPort > 0)
        {
            try
            {
                var bind = IPAddress.TryParse(r.Bind, out var a) ? a : IPAddress.IPv6Any;
                var host = new ShareHost(new IPEndPoint(bind, r.ListenPort), _identity, () => r.Peers, Find, version, _log);
                await host.StartAsync();
                _host = host;
            }
            catch (Exception ex) when (ex is QuicException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
            {
                ListenError = ex.Message;
                _log.LogError("Remote sharing: cannot listen on udp {Bind}:{Port}: {Error}", r.Bind, r.ListenPort, ex.Message);
            }
        }

        var tokens = LoadTokens();
        var brokers = new Dictionary<string, BrokerClient>();
        foreach (var inst in r.Instances.Where(i => i.Enabled))
        {
            if (!tokens.TryGetValue(inst.Id, out var token)) continue;
            var shared = inst.Devices.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var provider = new BrokerClient.Provider(
                () => [.. shared.Select(s => (Serial: s, Manager: Find(s))).Where(x => x.Manager is not null)
                    .Select(x => new AnnouncedShare(x.Serial, x.Manager!.Config.Name, x.Manager.Tuner?.Name ?? "", StateOf(x.Manager),
                        x.Manager.Details.Location, x.Manager.Details.Antenna))],
                s => shared.Contains(s) ? Find(s) : null);
            var client = new BrokerClient(new BrokerClient.Options(inst.Url, token, ServerNameOf(r), version), _identity, provider,
                loggers.CreateLogger($"instance.{(inst.Name.Length > 0 ? inst.Name : inst.Id)}"));
            client.Start();
            brokers[inst.Id] = client;
        }
        _brokers = brokers;
    }

    private static string StateOf(DeviceManager m) => m.State switch
    {
        DeviceState.Ready when m.Owner is not null => "busy",
        DeviceState.Ready => "ready",
        DeviceState.NotReady => "device_not_ready",
        DeviceState.Lost => "device_lost",
        _ => "idle",
    };

    public async Task StopAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null) await host.DisposeAsync();
        var brokers = _brokers;
        _brokers = [];
        foreach (var b in brokers.Values) await b.DisposeAsync();
    }

    // ---------------------------------------------------------------- enrollment / tokens

    /// <summary>Enrolls in an instance and stores the token. Returns the new config entry (not yet saved).</summary>
    public async Task<InstanceConfig> EnrollAsync(string url, string code, string password, string serverName,
        string version, CancellationToken ct)
    {
        var e = await BrokerClient.EnrollAsync(url, code, password, ServerId, serverName, version, ct);
        var inst = new InstanceConfig { Id = Guid.NewGuid().ToString("N")[..12], Url = url.TrimEnd('/'), Name = e.InstanceName, DeviceId = e.DeviceId };
        lock (_tokenLock)
        {
            var tokens = LoadTokens();
            tokens[inst.Id] = e.Token;
            SaveTokens(tokens);
        }
        _log.LogInformation("Enrolled in instance {Name} at {Url} (device {Device})", e.InstanceName, url, e.DeviceId);
        return inst;
    }

    public void ForgetToken(string instanceId)
    {
        lock (_tokenLock)
        {
            var tokens = LoadTokens();
            if (tokens.Remove(instanceId)) SaveTokens(tokens);
        }
    }

    private string TokenFile => Path.Combine(_dir, "tokens.json");

    private Dictionary<string, string> LoadTokens()
    {
        lock (_tokenLock)
        {
            try
            {
                return File.Exists(TokenFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenFile)) ?? []
                    : [];
            }
            catch (JsonException ex)
            {
                _log.LogError("Cannot read {File}: {Error}", TokenFile, ex.Message);
                return [];
            }
        }
    }

    /// <summary>Owner-only file on Unix (tokens are secrets), written atomically.</summary>
    private void SaveTokens(Dictionary<string, string> tokens)
    {
        Directory.CreateDirectory(_dir);
        var tmp = TokenFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(tokens));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, TokenFile, overwrite: true);
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
