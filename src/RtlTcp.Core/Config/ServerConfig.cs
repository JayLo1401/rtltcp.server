using System.Text.Json;
using System.Text.Json.Serialization;
using RtlTcp.Core.Auth;

namespace RtlTcp.Core.Config;

public sealed class ServerConfig
{
    public WebConfig Web { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public List<DeviceConfig> Devices { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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

public sealed class AuthConfig
{
    public bool Enabled { get; set; }
    public List<string> TrustedNetworks { get; set; } = [.. AccessPolicy.DefaultTrustedNetworks];
}

public sealed class DeviceConfig
{
    /// <summary>USB serial number. Empty = first stick that is not claimed by another entry.</summary>
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "RTL-SDR";
    public bool Enabled { get; set; } = true;
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 1234;

    /// <summary>Close the stick this many seconds after the last client left. 0 = keep it open all the time.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>How long a v1 client is held while the stick is not ready, before the connection is closed.</summary>
    public int V1WaitForDeviceSeconds { get; set; } = 10;

    public DeviceDefaults Defaults { get; set; } = new();
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
