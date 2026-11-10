using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtlTcp.Core.Config;

public sealed class ServerConfig
{
    public WebConfig Web { get; set; } = new();

    /// <summary>Port of the first stick; further sticks get the following ports. Chosen during installation.</summary>
    public int StreamBasePort { get; set; } = 1234;
    public LogConfig Logging { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public RemoteConfig Remote { get; set; } = new();
    public List<DeviceConfig> Devices { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Deep copy (via JSON) so changes can be validated and saved before they become visible.</summary>
    public static ServerConfig Clone(ServerConfig c) =>
        JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(c, Json), Json)!;

    public static ServerConfig Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? new ServerConfig()
            : new ServerConfig();

    /// <summary>Writes atomically (temp file + rename) so a crash never leaves a half-written config.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class WebConfig
{
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
}

public sealed class UpdateConfig
{
    /// <summary>Install new releases automatically at <see cref="Time"/> (Windows installer / Linux package only).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Local time "HH:mm" for the daily automatic update.</summary>
    public string Time { get; set; } = "04:00";
}

public sealed class LogConfig
{
    /// <summary>Daily log files older than this are deleted (1–365).</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Also log each IQ connection's details at debug level.</summary>
    public bool Debug { get; set; }
}

public sealed class DeviceConfig
{
    /// <summary>USB serial number. Empty = first stick that is not claimed by another entry.</summary>
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "RTL-SDR";

    /// <summary>Where the antenna is, e.g. "Wien Dach" (free text, shown to clients). For remote sticks the provider's value wins.</summary>
    public string Location { get; set; } = "";

    /// <summary>Antenna type, e.g. "DAB-Rundstrahler", "Discone" (free text, shown to clients).</summary>
    public string Antenna { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 1234;

    /// <summary>Close the stick this many seconds after the last client left. 0 = keep it open all the time.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>How long a v1 client is held while the stick is not ready, before the connection is closed.</summary>
    public int V1WaitForDeviceSeconds { get; set; } = 10;

    public DeviceDefaults Defaults { get; set; } = new();

    /// <summary>Set: not a USB stick but a stick shared by another rtltcp.server (Standort 2). <see cref="Serial"/> is unused.</summary>
    public RemoteDeviceConfig? Remote { get; set; }
}

/// <summary>
/// Sharing sticks with other rtltcp.server installations over QUIC (Standort 1, provider side).
/// For now peers are entered manually (test mode, needs a reachable UDP port); later rtl.over.internet
/// supplies peers, addresses and fingerprints per session.
/// </summary>
public sealed class RemoteConfig
{
    /// <summary>UDP port the QUIC listener uses; 0 = do not accept remote clients.</summary>
    public int ListenPort { get; set; }
    public string Bind { get; set; } = "::";
    public List<RemotePeerConfig> Peers { get; set; } = [];

    /// <summary>Name shown to other sites (rtl.over.internet); empty = computer name.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Instances on rtl.over.internet this server is enrolled in (token stored separately).</summary>
    public List<InstanceConfig> Instances { get; set; } = [];
}

/// <summary>Enrollment in one instance on rtl.over.internet. The device token lives in remote/tokens.json.</summary>
public sealed class InstanceConfig
{
    /// <summary>Local id (also key of the token).</summary>
    public string Id { get; set; } = "";
    public string Url { get; set; } = "https://rtl.jaycloud.at";
    public string Name { get; set; } = "";
    public int DeviceId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Serials of our sticks shared in this instance.</summary>
    public List<string> Devices { get; set; } = [];
}

/// <summary>Another rtltcp.server allowed to use some of our sticks, identified by its certificate fingerprint.</summary>
public sealed class RemotePeerConfig
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the peer's certificate (hex, colons optional), shown in its web GUI.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Serials of the sticks this peer may use.</summary>
    public List<string> Devices { get; set; } = [];
}

/// <summary>Where a remote stick lives (Standort 2, user side).</summary>
public sealed class RemoteDeviceConfig
{
    /// <summary>
    /// Set: the stick is shared in this instance (<see cref="InstanceConfig.Id"/>) and <see cref="Share"/> is the
    /// broker's share_id ("&lt;provider&gt;:&lt;serial&gt;"); host, port and fingerprint are not used.
    /// Empty: test mode with a fixed address.
    /// </summary>
    public string Instance { get; set; } = "";

    /// <summary>Host name or IP of the providing rtltcp.server.</summary>
    public string Host { get; set; } = "";

    /// <summary>Its QUIC (UDP) port, see <see cref="RemoteConfig.ListenPort"/>.</summary>
    public int Port { get; set; }

    /// <summary>SHA-256 of the provider's certificate; any other certificate is rejected.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Serial of the shared stick at the provider.</summary>
    public string Share { get; set; } = "";
}

/// <summary>Settings applied whenever a new client takes over the stick.</summary>
public sealed class DeviceDefaults
{
    public ulong Frequency { get; set; } = 100_000_000;
    public uint SampleRate { get; set; } = 2_048_000;

    /// <summary>Manual gain in tenths of dB; null = tuner AGC.</summary>
    public int? Gain { get; set; }

    public int Ppm { get; set; }
    public bool BiasTee { get; set; }
}
