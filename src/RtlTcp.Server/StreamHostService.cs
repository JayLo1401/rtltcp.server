using System.Reflection;
using RtlTcp.Core.Auth;
using RtlTcp.Core.Config;
using RtlTcp.Core.Device;
using RtlTcp.Core.Server;
using RtlTcp.Usb.LibUsb;

namespace RtlTcp.Server;

/// <summary>Starts one DeviceManager + StreamServer per configured stick.</summary>
public sealed class StreamHostService(ServerConfigHolder config, UserStore users, ILoggerFactory loggers) : IHostedService
{
    public static string Version { get; } =
        typeof(StreamHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private readonly ILogger _log = loggers.CreateLogger("rtltcp");
    private readonly List<(DeviceManager Manager, StreamServer Server)> _devices = [];
    private LibUsbBackend? _usb;

    public IReadOnlyList<DeviceManager> Managers => _devices.Select(d => d.Manager).ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("rtltcp.server {Version}, data directory {Dir}", Version, config.DataDir);
        _usb = new LibUsbBackend();
        var source = new RtlSdrDeviceSource(_usb);

        var cfg = config.Current;
        if (cfg.Devices.Count == 0) AutoConfigure(cfg, source);

        foreach (var dc in cfg.Devices.Where(d => d.Enabled))
        {
            var others = cfg.Devices.Where(d => d != dc && d.Serial.Length > 0).Select(d => d.Serial).ToArray();
            var manager = new DeviceManager(dc, source, loggers.CreateLogger($"device.{dc.Port}"), () => others);
            var server = new StreamServer(manager, () => config.Policy, users, Version, loggers.CreateLogger($"stream.{dc.Port}"));
            try
            {
                server.Start();
                _devices.Add((manager, server));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                _log.LogError("{Device}: cannot listen on {Bind}:{Port}: {Error}", dc.Name, dc.Bind, dc.Port, ex.Message);
                _ = manager.DisposeAsync();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>First start: one entry per connected stick, ports from 1234 upwards.</summary>
    private void AutoConfigure(ServerConfig cfg, ISdrDeviceSource source)
    {
        var sticks = source.List().Where(s => s.Serial is not null).ToList();
        var port = 1234;
        foreach (var s in sticks)
            cfg.Devices.Add(new DeviceConfig { Serial = s.Serial!, Name = $"RTL-SDR {s.Serial}", Port = port++ });
        if (cfg.Devices.Count == 0)
            cfg.Devices.Add(new DeviceConfig { Serial = "", Name = "RTL-SDR", Port = 1234 }); // first stick that shows up
        config.Save();
        _log.LogInformation("Created {Path} with {Count} device(s)", config.ConfigPath, cfg.Devices.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (manager, server) in _devices)
        {
            await server.DisposeAsync();
            await manager.DisposeAsync();
        }
        _devices.Clear();
        _usb?.Dispose();
    }
}

/// <summary>Current configuration; the web GUI will update it at runtime.</summary>
public sealed class ServerConfigHolder
{
    public ServerConfigHolder(string dataDir)
    {
        DataDir = dataDir;
        ConfigPath = Path.Combine(dataDir, "config.json");
        Current = ServerConfig.Load(ConfigPath);
        Policy = BuildPolicy();
    }

    public string DataDir { get; }
    public string ConfigPath { get; }
    public ServerConfig Current { get; }
    public AccessPolicy Policy { get; private set; }

    public void Save()
    {
        Current.Save(ConfigPath);
        Policy = BuildPolicy();
    }

    private AccessPolicy BuildPolicy() => new(Current.Auth.Enabled, Current.Auth.TrustedNetworks);
}
