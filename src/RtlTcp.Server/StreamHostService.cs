using System.Reflection;
using RtlTcp.Core.Auth;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Server;
using RtlTcp.Core.Usb;
using RtlTcp.Usb.LibUsb;

namespace RtlTcp.Server;

/// <summary>Starts one DeviceManager + StreamServer per configured stick; can restart them after config changes.</summary>
public sealed class StreamHostService(ServerConfigHolder config, UserStore users, ILoggerFactory loggers) : IHostedService
{
    public static string Version { get; } =
        typeof(StreamHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private readonly ILogger _log = loggers.CreateLogger("rtltcp");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<(DeviceManager Manager, StreamServer Server)> _devices = [];
    private LibUsbBackend? _usb;
    private RtlSdrDeviceSource? _source;
    private readonly RemoteSharing _remote = new(config.DataDir, loggers.CreateLogger("remote"));

    /// <summary>Sharing sticks between sites (QUIC): own fingerprint, availability, listener state.</summary>
    public RemoteSharing Remote => _remote;

    /// <summary>Running devices, keyed by their config entry.</summary>
    public IReadOnlyList<DeviceManager> Managers => _devices.Select(d => d.Manager).ToList();

    /// <summary>Error per port that could not be started (e.g. port in use).</summary>
    public IReadOnlyDictionary<int, string> StartErrors { get; private set; } = new Dictionary<int, string>();

    public IReadOnlyList<UsbDeviceInfo> ConnectedSticks() => _source?.List() ?? [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("rtltcp.server {Version}, data directory {Dir}", Version, config.DataDir);
        _usb = new LibUsbBackend();
        _source = new RtlSdrDeviceSource(_usb);
        if (config.Current.Devices.Count == 0) AutoConfigure(config.Current, _source);

        _remote.Init();
        await _gate.WaitAsync(cancellationToken);
        try { await StartDevicesAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>Applies a changed device list: stops all stream servers (clients are disconnected) and starts them again.</summary>
    public async Task ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopDevicesAsync();
            await StartDevicesAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartDevicesAsync()
    {
        var cfg = config.Current;
        var started = new List<(DeviceManager, StreamServer)>();
        var errors = new Dictionary<int, string>();
        foreach (var dc in cfg.Devices.Where(d => d.Enabled))
        {
            var others = cfg.Devices.Where(d => d != dc && d.Serial.Length > 0 && d.Remote is null).Select(d => d.Serial).ToArray();
            var source = dc.Remote is { } remote ? _remote.SourceFor(remote, loggers.CreateLogger($"remote.{dc.Port}")) : _source!;
            var manager = new DeviceManager(dc, source, loggers.CreateLogger($"device.{dc.Port}"), () => others);
            var server = new StreamServer(manager, () => config.Policy, users, Version, loggers.CreateLogger($"stream.{dc.Port}"));
            try
            {
                server.Start();
                started.Add((manager, server));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                _log.LogError("{Device}: cannot listen on {Bind}:{Port}: {Error}", dc.Name, dc.Bind, dc.Port, ex.Message);
                errors[dc.Port] = ex.Message;
                _ = manager.DisposeAsync();
            }
        }
        _devices = started;
        StartErrors = errors;
        await _remote.StartShareHostAsync(cfg.Remote, users, Version,
            share => _devices.Select(d => d.Manager).FirstOrDefault(m => m.Config.Remote is null &&
                (string.Equals(m.Config.Serial, share, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(m.OpenSerial, share, StringComparison.OrdinalIgnoreCase))));
    }

    private async Task StopDevicesAsync()
    {
        await _remote.StopShareHostAsync();
        var devices = _devices;
        _devices = [];
        foreach (var (manager, server) in devices)
        {
            await server.DisposeAsync();
            await manager.DisposeAsync();
        }
    }

    /// <summary>First start: one entry per connected stick, ports from 1234 upwards.</summary>
    private void AutoConfigure(ServerConfig cfg, ISdrDeviceSource source)
    {
        var sticks = source.List().Where(s => s.Serial is not null).ToList();
        // RTLTCP_STREAM_PORT: set by the Linux installer (/etc/default/rtltcp-server)
        var port = int.TryParse(Environment.GetEnvironmentVariable("RTLTCP_STREAM_PORT"), out var p) && p is > 0 and < 65536
            ? p : cfg.StreamBasePort;
        foreach (var s in sticks)
            cfg.Devices.Add(new DeviceConfig { Serial = s.Serial!, Name = $"RTL-SDR {s.Serial}", Port = port++ });
        if (cfg.Devices.Count == 0)
            cfg.Devices.Add(new DeviceConfig { Serial = "", Name = "RTL-SDR", Port = port }); // first stick that shows up
        config.Save();
        _log.LogInformation("Created {Path} with {Count} device(s)", config.ConfigPath, cfg.Devices.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopDevicesAsync();
            _usb?.Dispose();
            _usb = null;
            _remote.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Current configuration; changed by the web GUI at runtime.</summary>
public sealed class ServerConfigHolder
{
    private readonly Lock _lock = new();

    public ServerConfigHolder(string dataDir)
    {
        DataDir = dataDir;
        ConfigPath = Path.Combine(dataDir, "config.json");
        Current = ServerConfig.Load(ConfigPath);
        Policy = BuildPolicy();
    }

    public string DataDir { get; }
    public string ConfigPath { get; }
    public ServerConfig Current { get; private set; }

    /// <summary>Read per stream connection, so auth changes apply to the next connection without restart.</summary>
    public AccessPolicy Policy { get; private set; }

    public void Save()
    {
        lock (_lock)
        {
            Current.Save(ConfigPath);
            Policy = BuildPolicy();
        }
    }

    /// <summary>Replaces parts of the configuration atomically and saves.</summary>
    public void Update(Action<ServerConfig> change)
    {
        lock (_lock)
        {
            var copy = ServerConfig.Clone(Current);
            change(copy);
            copy.Save(ConfigPath);
            Current = copy;
            Policy = BuildPolicy();
        }
    }

    private AccessPolicy BuildPolicy() => new(Current.Auth.Enabled, Current.Auth.TrustedNetworks);
}
