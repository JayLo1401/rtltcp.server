using System.ServiceProcess;
using System.Text.Json;

namespace RtlTcp.Tray;

internal sealed record DeviceInfo(string Name, int Port, string State, string Message, string? Client);

/// <summary>What the tray shows: service state plus (while running) the sticks.</summary>
internal sealed record Snapshot(ServiceControllerStatus? Service, IReadOnlyList<DeviceInfo>? Devices)
{
    /// <summary>Running, but a stick is missing / lost / failed to start.</summary>
    public bool HasProblem => Devices?.Any(d => d.State is "notready" or "lost" or "error") == true;
}

/// <summary>Reads the web address from config.json and the status from /api/tray (localhost only, no login).</summary>
internal sealed class ServerStatus : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rtltcp.server", "config.json");

    /// <summary>Base URL of the web interface, e.g. http://localhost:8080/.</summary>
    public static string WebUrl()
    {
        var host = "localhost";
        var port = 8080;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("web", out var web))
            {
                if (web.TryGetProperty("port", out var p) && p.TryGetInt32(out var v)) port = v;
                if (web.TryGetProperty("bind", out var b) && b.GetString() is { } bind
                    && bind is not ("0.0.0.0" or "::" or "127.0.0.1" or "::1" or ""))
                    host = bind.Contains(':') ? $"[{bind}]" : bind;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // defaults
        }
        return $"http://{host}:{port}/";
    }

    public async Task<Snapshot> GetAsync()
    {
        var service = ServiceControl.Status();
        if (service != ServiceControllerStatus.Running) return new Snapshot(service, null);
        try
        {
            var url = WebUrl().Replace("//localhost:", "//127.0.0.1:") + "api/tray";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var devices = new List<DeviceInfo>();
            foreach (var d in doc.RootElement.GetProperty("devices").EnumerateArray())
                devices.Add(new DeviceInfo(
                    d.GetProperty("name").GetString() ?? "",
                    d.GetProperty("port").GetInt32(),
                    d.GetProperty("state").GetString() ?? "",
                    d.GetProperty("message").GetString() ?? "",
                    d.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null));
            return new Snapshot(service, devices);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            // Service runs, web interface not (yet) reachable: still "running", without stick details.
            return new Snapshot(service, null);
        }
    }

    public void Dispose() => _http.Dispose();
}
