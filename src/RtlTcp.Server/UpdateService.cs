using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RtlTcp.Core.Update;
using RtlTcp.Server.Web;

namespace RtlTcp.Server;

public enum UpdateMode
{
    /// <summary>Installed with the Windows installer: download setup.exe and run it silently.</summary>
    Windows,
    /// <summary>Installed from the .deb: a root systemd unit installs the update on request.</summary>
    Linux,
    /// <summary>Home Assistant add-on: HA updates it.</summary>
    HomeAssistant,
    /// <summary>Started from source / unknown installation: only show that an update exists.</summary>
    Manual,
}

/// <summary>
/// Checks repository.jaycloud.at for new releases (at start and every 6 h), installs them automatically at the
/// configured time (default 04:00) or when the admin clicks the button in the web interface.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    public const string DefaultBaseUrl = "https://repository.jaycloud.at/repository/rtltcp.server";
    private const string LinuxUpdateScript = "/opt/rtltcp-server/update.sh";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ServerConfigHolder _config;
    private readonly StreamHostService _host;
    private readonly ILogger<UpdateService> _log;
    private readonly string _baseUrl;
    private readonly string _updatesDir;
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private DateOnly? _lastAutoRun;

    public UpdateService(ServerConfigHolder config, StreamHostService host, ILogger<UpdateService> log)
    {
        _config = config;
        _host = host;
        _log = log;
        _baseUrl = (Environment.GetEnvironmentVariable("RTLTCP_UPDATE_BASE") is { Length: > 0 } b ? b : DefaultBaseUrl).TrimEnd('/');
        _updatesDir = Path.Combine(config.DataDir, "updates");
        _pendingPath = Path.Combine(config.DataDir, "update-pending.json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"rtltcp-server/{StreamHostService.Version}");
        Mode = DetectMode(config.DataDir);
    }

    public UpdateMode Mode { get; }
    public string CurrentVersion => StreamHostService.Version;
    public ReleasePackage? Latest { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }

    /// <summary>idle, checking, downloading, installing, failed, done</summary>
    public string State { get; private set; } = "idle";
    public string? Message { get; private set; }

    public bool UpdateAvailable => Latest is not null && ReleaseManifest.IsNewer(Latest.Version, CurrentVersion);
    public bool CanInstall => Mode is UpdateMode.Windows or UpdateMode.Linux;

    private static UpdateMode DetectMode(string dataDir)
    {
        if (WebSetup.IngressEnabled) return UpdateMode.HomeAssistant;
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")))
            return UpdateMode.Windows;
        if (OperatingSystem.IsLinux() && File.Exists(LinuxUpdateScript)
            && Path.GetFullPath(dataDir).TrimEnd('/') == "/var/lib/rtltcp-server")
            return UpdateMode.Linux;
        return UpdateMode.Manual;
    }

    // ---------------------------------------------------------------- loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CheckPendingResult();
        if (Mode == UpdateMode.HomeAssistant) return; // Home Assistant shows and installs add-on updates

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            await CheckAsync(ct);
            using var tick = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await tick.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (LastCheck is null || DateTimeOffset.Now - LastCheck > CheckInterval) await CheckAsync(ct);
                    if (State == "installing") PollLinuxStatus();
                    await AutoUpdateAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Update loop: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AutoUpdateAsync(CancellationToken ct)
    {
        var cfg = _config.Current.Update;
        if (!cfg.AutoUpdate || !CanInstall || !UpdateSchedule.TryParseTime(cfg.Time, out var time)) return;
        var now = DateTime.Now;
        if (!UpdateSchedule.IsDue(now, time, _lastAutoRun)) return;

        await CheckAsync(ct);
        if (!UpdateAvailable)
        {
            _lastAutoRun = DateOnly.FromDateTime(now);
            return;
        }
        // Do not cut off a running client unless the window is about to end.
        var busy = _host.Managers.Where(m => m.Owner is not null).Select(m => m.Owner!.RemoteAddress).ToList();
        if (busy.Count > 0 && !UpdateSchedule.IsWindowEnding(now, time))
        {
            _log.LogDebug("Automatic update waits, clients connected: {Clients}", string.Join(", ", busy));
            return;
        }
        _lastAutoRun = DateOnly.FromDateTime(now);
        _log.LogInformation("Automatic update to {Version} (scheduled {Time})", Latest!.Version, cfg.Time);
        await InstallAsync("automatisch", ct);
    }

    // ---------------------------------------------------------------- check

    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (Mode == UpdateMode.HomeAssistant) return false;
        try
        {
            if (State is "idle" or "failed" or "done") State = "checking";
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            var json = await _http.GetStringAsync($"{_baseUrl}/{platform}/release/latest/version.json", ct);
            var latest = OperatingSystem.IsWindows() ? ReleaseManifest.ParseWindows(json) : ReleaseManifest.ParseLinux(json, DebArch());
            LastCheck = DateTimeOffset.Now;
            if (latest is null)
            {
                _log.LogWarning("Update check: no valid package in version.json");
            }
            else
            {
                if (UpdateAvailable == false && ReleaseManifest.IsNewer(latest.Version, CurrentVersion))
                    _log.LogInformation("Update available: {Version} (installed {Current})", latest.Version, CurrentVersion);
                Latest = latest;
            }
            if (State == "checking") State = "idle";
            return UpdateAvailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LastCheck = DateTimeOffset.Now;
            if (State == "checking") State = "idle";
            _log.LogWarning("Update check failed: {Error}", ex.Message);
            return false;
        }
    }

    private static string DebArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "armhf",
        var a => a.ToString().ToLowerInvariant(),
    };

    // ---------------------------------------------------------------- install

    /// <summary>Installs <see cref="Latest"/>. Returns an error text or null when the installation was started.</summary>
    public async Task<string?> InstallAsync(string reason, CancellationToken ct = default)
    {
        if (!CanInstall) return "Diese Installation kann sich nicht selbst aktualisieren.";
        if (!await _gate.WaitAsync(0, ct)) return "Ein Update läuft bereits.";
        try
        {
            await CheckAsync(ct);
            if (!UpdateAvailable) return "Bereits aktuell.";
            var pkg = Latest!;
            _log.LogInformation("Installing update {Version} ({Reason})", pkg.Version, reason);
            WritePending(pkg.Version);
            if (Mode == UpdateMode.Windows) await InstallWindowsAsync(pkg, ct);
            else RequestLinuxInstall(pkg);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            Fail($"Update fehlgeschlagen: {ex.Message}");
            File.Delete(_pendingPath);
            return Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InstallWindowsAsync(ReleasePackage pkg, CancellationToken ct)
    {
        Directory.CreateDirectory(_updatesDir);
        foreach (var old in Directory.EnumerateFiles(_updatesDir, "rtltcp-server-setup-*")) File.Delete(old);
        var setup = Path.Combine(_updatesDir, $"rtltcp-server-setup-{pkg.Version}.exe");

        State = "downloading";
        Message = $"Lade Version {pkg.Version} …";
        await using (var src = await _http.GetStreamAsync(pkg.Url, ct))
        await using (var dst = File.Create(setup))
            await src.CopyToAsync(dst, ct);
        VerifySha256(setup, pkg.Sha256);

        // The installer stops this service, replaces the files and starts it again. It keeps running on its own
        // when the service process ends.
        State = "installing";
        Message = $"Version {pkg.Version} wird installiert, der Dienst startet gleich neu …";
        _log.LogInformation("Starting installer {Setup}", setup);
        Process.Start(new ProcessStartInfo(setup,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=\"{Path.Combine(_updatesDir, $"setup-{pkg.Version}.log")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _updatesDir,
        });
    }

    /// <summary>
    /// The service runs as unprivileged user. It only drops a request file; the root unit
    /// rtltcp-server-update.path/.service downloads the official package, checks the SHA-256 and installs it.
    /// </summary>
    private void RequestLinuxInstall(ReleasePackage pkg)
    {
        State = "installing";
        Message = $"Version {pkg.Version} wird installiert, der Dienst startet gleich neu …";
        File.Delete(Path.Combine(_config.DataDir, "update-status")); // result of an earlier attempt
        File.WriteAllText(Path.Combine(_config.DataDir, "update-request"), pkg.Version);
    }

    private static void VerifySha256(string file, string expected)
    {
        using var s = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(s));
        if (actual != expected)
        {
            s.Dispose();
            File.Delete(file);
            throw new InvalidDataException("Prüfsumme des Downloads stimmt nicht");
        }
    }

    // ---------------------------------------------------------------- result after restart

    private sealed record Pending(string Target, string From, DateTimeOffset Started);

    private void WritePending(string target) =>
        File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new Pending(target, CurrentVersion, DateTimeOffset.Now)));

    /// <summary>After a restart: did the update we started succeed?</summary>
    private void CheckPendingResult()
    {
        if (!File.Exists(_pendingPath)) return;
        try
        {
            var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(_pendingPath));
            if (p is null) return;
            if (ReleaseManifest.ParseVersion(CurrentVersion) == ReleaseManifest.ParseVersion(p.Target))
            {
                State = "done";
                Message = $"Update von {p.From} auf {p.Target} installiert.";
                _log.LogInformation("Update from {From} to {To} installed", p.From, p.Target);
                File.Delete(_pendingPath);
            }
            else if (DateTimeOffset.Now - p.Started > TimeSpan.FromMinutes(15))
            {
                Fail($"Update auf {p.Target} wurde nicht abgeschlossen. {LinuxStatusMessage() ?? "Details im Protokoll bzw. im Installer-Log (updates\\setup-*.log)."}");
                File.Delete(_pendingPath);
            }
            else
            {
                State = "installing"; // still running (Linux unit, or installer not finished yet)
                Message = $"Update auf {p.Target} läuft …";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            File.Delete(_pendingPath);
        }
    }

    /// <summary>Linux: the root update script reports failures in update-status (no restart happens then).</summary>
    private void PollLinuxStatus()
    {
        if (Mode != UpdateMode.Linux) { CheckPendingResult(); return; }
        var path = Path.Combine(_config.DataDir, "update-status");
        if (!File.Exists(path) || File.GetLastWriteTime(path) < DateTime.Now.AddHours(-1)) { CheckPendingResult(); return; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var state = doc.RootElement.GetProperty("state").GetString();
            var msg = doc.RootElement.GetProperty("message").GetString();
            if (state == "failed")
            {
                Fail($"Update fehlgeschlagen: {msg}");
                File.Delete(_pendingPath);
            }
            else if (state == "current")
            {
                State = "idle";
                Message = msg;
                File.Delete(_pendingPath);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException) { }
    }

    private string? LinuxStatusMessage()
    {
        var path = Path.Combine(_config.DataDir, "update-status");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("message").GetString();
        }
        catch (Exception) { return null; }
    }

    private void Fail(string message)
    {
        State = "failed";
        Message = message;
        _log.LogError("{Message}", message);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
