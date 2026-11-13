using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RtlTcp.Core.Config;
using RtlTcp.Core.Rtl2832;
using RtlTcp.Server.Logging;

namespace RtlTcp.Server.Web;

/// <summary>REST API for the web GUI. Everything except session/login/status (read only) requires an admin cookie.</summary>
public static class WebApi
{
    /// <summary>Custom header required on every state-changing request (blocks cross-site form posts).</summary>
    public const string CsrfHeader = "X-RTLTCP";

    public sealed record SessionDto(bool LoggedIn, string? User, string Version, bool Ingress, bool MustChangePassword, bool DefaultLoginHint);
    public sealed record CredentialsDto(string User, string Password);
    public sealed record PasswordChangeDto(string Current, string New);

    public sealed record DeviceStatusDto(
        int Port, string Name, string Serial, bool Enabled, string State, string Message,
        string? Client, DateTimeOffset? ClientSince, long BytesStreamed,
        string? Tuner, ulong? Frequency, uint? SampleRate, int? Gain, bool BiasTee, string? Error,
        string? ClientProtocol = null, string? Origin = null, string? Location = null, string? Antenna = null,
        bool IsRemote = false, long ClientBytesSent = 0, long ClientDropped = 0, bool ClientIsRemoteSite = false);

    /// <param name="BytesStreamed">IQ bytes from the stick (remote stick: received over the internet).</param>
    /// <param name="ClientBytesSent">Bytes written to the current client's connection (for the real data rate).</param>
    public sealed record StatusDto(string Version, List<DeviceStatusDto> Devices, List<StickDto> Sticks,
        string? UpdateVersion, bool UpdateCanInstall, RemoteStatusDto Remote);

    /// <summary>Sharing between sites: why it is unavailable (null = available).</summary>
    public sealed record RemoteStatusDto(string? Error);

    public sealed record RemoteSettingsDto(string ServerName);

    public sealed record EnrollDto(string Url, string Code, string Password);
    public sealed record InstanceUpdateDto(bool Enabled, List<string> Devices);
    public sealed record InstanceShareDto(string ShareId, string Provider, string StickId, string Name, string Tuner, string State,
        bool Online, bool Own, string? Location = null, string? Antenna = null);
    public sealed record InstanceDto(string Id, string Url, string Name, bool Enabled, List<string> Devices, string State,
        string? Message, int DeviceId, List<InstanceShareDto> Shares);
    public sealed record RemoteInstancesDto(string ServerName, string? Fingerprint, string? Unavailable, List<InstanceDto> Instances);

    public sealed record UpdateDto(string Mode, string Current, string? Latest, bool Available, bool CanInstall,
        DateTimeOffset? LastCheck, string State, string? Message, bool AutoUpdate, string Time);

    public sealed record UpdateSettingsDto(bool AutoUpdate, string Time);

    public sealed record TrayDeviceDto(string Name, int Port, string State, string Message, string? Client);
    public sealed record TrayDto(string Version, List<TrayDeviceDto> Devices);

    private static UpdateDto ToDto(UpdateService u, ServerConfigHolder cfg) =>
        new(u.Mode.ToString().ToLowerInvariant(), u.CurrentVersion, u.Latest?.Version, u.UpdateAvailable, u.CanInstall,
            u.LastCheck, u.State, u.Message, cfg.Current.Update.AutoUpdate, cfg.Current.Update.Time);
    public sealed record StickDto(string? Serial, string Name, string Location, bool Configured);

    public static void Map(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("web");
        static string Ip(HttpContext c) => ClientIp(c).ToString();
        static string Who(HttpContext c) => $"{c.User.Identity?.Name} ({Ip(c)})";
        var api = app.MapGroup("/api");

        api.MapGet("/session", (HttpContext ctx, UserStore users, ServerConfigHolder cfg) =>
        {
            var ingress = WebSetup.IsIngress(ctx);
            var name = ctx.User.Identity?.Name;
            return new SessionDto(ctx.User.Identity?.IsAuthenticated == true, name, StreamHostService.Version, ingress,
                MustChangePassword: !ingress && users.MustChangePassword(name),
                DefaultLoginHint: users.MustChangePassword(UserStore.DefaultAdmin) && IsLocal(ctx));
        });

        api.MapPost("/login", async (HttpContext ctx, CredentialsDto dto, UserStore users, LoginThrottle throttle, ServerConfigHolder cfg) =>
        {
            var ip = ClientIp(ctx);
            if (throttle.IsBlocked(ip, out var wait))
            {
                log.LogWarning("Web login from {Ip} blocked (too many failures)", ip);
                return Problem($"Zu viele Fehlversuche. Bitte in {Math.Ceiling(wait.TotalMinutes)} Minuten erneut versuchen.", 429);
            }
            if (!users.VerifyAdmin(dto.User ?? "", dto.Password ?? ""))
            {
                throttle.Failed(ip);
                log.LogWarning("Web login failed for {User} from {Ip}", dto.User, ip);
                await Task.Delay(500);
                return Problem("Benutzer oder Passwort falsch", 401);
            }
            throttle.Succeeded(ip);
            if (users.MustChangePassword(dto.User) && !IsLocal(ctx))
            {
                log.LogWarning("Web login with default password from {Ip} refused (not a local network)", ip);
                return Problem("Das Standardpasswort ist nur aus dem lokalen Netzwerk erlaubt. Bitte dort anmelden und ändern.", 403);
            }
            log.LogInformation("Web login: {User} from {Ip}", dto.User, ip);
            await SignInAsync(ctx, dto.User!);
            return Results.Ok();
        });

        api.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        // Windows tray icon (rtltcp-tray.exe): short status without login, only from this computer.
        api.MapGet("/tray", (HttpContext ctx, StreamHostService host, ServerConfigHolder cfg) =>
        {
            var remote = ClientIp(ctx);
            var local = ctx.Connection.LocalIpAddress is { } l && l.IsIPv4MappedToIPv6 ? l.MapToIPv4() : ctx.Connection.LocalIpAddress;
            if (!IPAddress.IsLoopback(remote) && !remote.Equals(local)) return Results.NotFound();
            var managers = host.Managers.ToDictionary(m => m.Config.Port);
            var devices = cfg.Current.Devices.Where(d => d.Enabled).Select(d =>
            {
                managers.TryGetValue(d.Port, out var m);
                host.StartErrors.TryGetValue(d.Port, out var error);
                return new TrayDeviceDto(d.Name, d.Port, m is null ? "error" : m.State.ToString().ToLowerInvariant(),
                    m?.StateMessage ?? error ?? "Nicht gestartet", m?.Owner?.RemoteAddress);
            }).ToList();
            return Results.Ok(new TrayDto(StreamHostService.Version, devices));
        });

        var admin = api.MapGroup("").RequireAuthorization();

        // Until the default password is changed, only the password change itself is allowed.
        admin.AddEndpointFilter(async (fc, next) =>
        {
            var ctx = fc.HttpContext;
            var users = ctx.RequestServices.GetRequiredService<UserStore>();
            if (users.MustChangePassword(ctx.User.Identity?.Name) && !ctx.Request.Path.StartsWithSegments("/api/admin/password"))
                return Problem("Bitte zuerst das Passwort ändern", 403);
            return await next(fc);
        });

        admin.MapGet("/update", (UpdateService u, ServerConfigHolder cfg) => ToDto(u, cfg));

        admin.MapPost("/update/check", async (UpdateService u, ServerConfigHolder cfg) =>
        {
            await u.CheckAsync();
            return ToDto(u, cfg);
        });

        admin.MapPost("/update/install", async (HttpContext ctx, UpdateService u, ServerConfigHolder cfg) =>
        {
            log.LogInformation("{Admin} started the update", Who(ctx));
            var error = await u.InstallAsync($"Weboberfläche, {Who(ctx)}");
            return error is null ? Results.Ok(ToDto(u, cfg)) : Problem(error, 409);
        });

        admin.MapPut("/settings/update", (HttpContext ctx, UpdateSettingsDto dto, ServerConfigHolder cfg) =>
        {
            if (!RtlTcp.Core.Update.UpdateSchedule.TryParseTime(dto.Time, out var t)) return Problem("Uhrzeit bitte als HH:MM, z. B. 04:00");
            cfg.Update(c => c.Update = new UpdateConfig { AutoUpdate = dto.AutoUpdate, Time = t.ToString("HH:mm") });
            log.LogInformation("{Admin} set automatic updates {State} at {Time}", Who(ctx), dto.AutoUpdate ? "ON" : "OFF", t.ToString("HH:mm"));
            return Results.Ok();
        });

        // Status is readable without login (like a status page); disconnecting a client needs the login.
        // Without login: no USB stick list and no update info (only needed for administration).
        api.MapGet("/status", (HttpContext ctx, StreamHostService host, ServerConfigHolder cfg, UpdateService update) =>
        {
            var loggedIn = ctx.User.Identity?.IsAuthenticated == true;
            var managers = host.Managers.ToDictionary(m => m.Config.Port);
            var devices = cfg.Current.Devices.Select(d =>
            {
                managers.TryGetValue(d.Port, out var m);
                host.StartErrors.TryGetValue(d.Port, out var error);
                var state = !d.Enabled ? "disabled" : m is null ? "error" : m.State.ToString().ToLowerInvariant();
                return new DeviceStatusDto(d.Port, d.Name, m?.OpenSerial ?? d.Serial, d.Enabled, state,
                    !d.Enabled ? "Deaktiviert" : m?.StateMessage ?? error ?? "Nicht gestartet",
                    m?.Owner?.RemoteAddress, m?.OwnerSince, m?.BytesStreamed ?? 0, m?.Tuner?.Name,
                    m?.Tuning.Frequency, m?.Tuning.SampleRate, m?.Tuning.Gain, m?.Tuning.BiasTee ?? false, error,
                    m?.Owner?.Protocol, d.Remote is null ? null : OriginText(m?.Origin()),
                    m?.Details.Location ?? NullIfEmpty(d.Location), m?.Details.Antenna ?? NullIfEmpty(d.Antenna),
                    d.Remote is not null, m?.Owner?.BytesSent ?? 0, m?.Owner?.DroppedBuffers ?? 0, m?.Owner?.IsRemoteSite ?? false);
            }).ToList();
            var r = host.Remote;
            if (!loggedIn) return new StatusDto(StreamHostService.Version, devices, [], null, false, new RemoteStatusDto(r.Unavailable));
            var configured = cfg.Current.Devices.Select(d => d.Serial).ToHashSet();
            // A stick that is open (by us) cannot be opened again to read its serial (Windows) → add our open sticks.
            var sticks = host.ConnectedSticks().Where(s => s.Serial is not null)
                .Select(s => new StickDto(s.Serial, KnownDevices.Name(s.VendorId, s.ProductId), s.Location, configured.Contains(s.Serial!)))
                .ToList();
            foreach (var m in host.Managers.Where(m => m.Config.Remote is null)) // remote sticks are not local USB sticks
                if (m.OpenSerial is { } serial && sticks.All(s => s.Serial != serial))
                    sticks.Add(new StickDto(serial, "in Verwendung", "", configured.Contains(serial)));
            return new StatusDto(StreamHostService.Version, devices, sticks,
                update.UpdateAvailable ? update.Latest!.Version : null, update.CanInstall,
                new RemoteStatusDto(r.Unavailable));
        });

        admin.MapPost("/devices/{port:int}/kick", (HttpContext ctx, int port, StreamHostService host) =>
        {
            var m = host.Managers.FirstOrDefault(x => x.Config.Port == port);
            if (m?.Owner is null) return Problem("Kein Client verbunden", 404);
            log.LogInformation("{Admin} disconnected client {Client} from port {Port}", Who(ctx), m.Owner.RemoteAddress, port);
            m.KickOwner();
            return Results.Ok();
        });

        admin.MapGet("/devices", (ServerConfigHolder cfg) => cfg.Current.Devices);

        admin.MapPut("/devices", async (HttpContext ctx, List<DeviceConfig> devices, ServerConfigHolder cfg, StreamHostService host) =>
        {
            if (ValidateDevices(devices, WebSetup.WebPorts(cfg)) is { } error) return Problem(error);
            cfg.Update(c => c.Devices = devices);
            log.LogInformation("{Admin} saved {Count} device(s), restarting stream servers", Who(ctx), devices.Count);
            await host.ReloadAsync();
            return Results.Ok();
        });

        admin.MapGet("/settings/remote", (ServerConfigHolder cfg) => new RemoteSettingsDto(cfg.Current.Remote.ServerName));

        // Only the server name: used for new enrollments and when the control channels start next time.
        admin.MapPut("/settings/remote", (HttpContext ctx, RemoteSettingsDto dto, ServerConfigHolder cfg) =>
        {
            var name = (dto.ServerName ?? "").Trim();
            if (name.Length > 64) return Problem("Name höchstens 64 Zeichen");
            if (name == cfg.Current.Remote.ServerName) return Results.Ok();
            cfg.Update(c => c.Remote.ServerName = name);
            log.LogInformation("{Admin} set the server name for rtl.over.internet to {Name}", Who(ctx), name);
            return Results.Ok();
        });

        // ---- instances on rtl.over.internet

        admin.MapGet("/remote/instances", (ServerConfigHolder cfg, StreamHostService host) =>
            new RemoteInstancesDto(RemoteSharing.ServerNameOf(cfg.Current.Remote), host.Remote.Fingerprint, host.Remote.Unavailable,
                cfg.Current.Remote.Instances.Select(i =>
                {
                    var b = host.Remote.Broker(i.Id);
                    return new InstanceDto(i.Id, i.Url, b?.InstanceName ?? i.Name, i.Enabled, i.Devices,
                        !i.Enabled ? "disabled" : b is null ? "stopped" : b.State.ToString().ToLowerInvariant(),
                        b?.Message, b?.DeviceId ?? i.DeviceId,
                        b?.Shares.Select(s => new InstanceShareDto(s.ShareId, s.ProviderName, s.StickId, s.Name, s.Tuner, s.State,
                            s.Online, s.ProviderId == (b.DeviceId ?? i.DeviceId), s.Location, s.Antenna)).ToList() ?? []);
                }).ToList()));

        admin.MapPost("/remote/instances", async (HttpContext ctx, EnrollDto dto, ServerConfigHolder cfg, StreamHostService host) =>
        {
            if (!Uri.TryCreate(dto.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
                return Problem("Adresse bitte als https://…");
            if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrEmpty(dto.Password))
                return Problem("Instanz-Code und Passwort angeben");
            if (host.Remote.Unavailable is { } why) return Problem(why);
            InstanceConfig inst;
            try
            {
                inst = await host.Remote.EnrollAsync(url.ToString(), dto.Code.Trim(), dto.Password,
                    RemoteSharing.ServerNameOf(cfg.Current.Remote), StreamHostService.Version, ctx.RequestAborted);
            }
            catch (RtlTcp.Core.Remote.Broker.EnrollException ex)
            {
                log.LogWarning("{Admin}: enrollment at {Url} failed: {Error}", Who(ctx), url, ex.Message);
                return Problem(ex.Message);
            }
            cfg.Update(c =>
            {
                // the broker replaces the token of an earlier enrollment of this server in the same instance
                c.Remote.Instances.RemoveAll(i => i.Url == inst.Url && i.DeviceId == inst.DeviceId);
                c.Remote.Instances.Add(inst);
            });
            log.LogInformation("{Admin} added instance {Name} ({Url})", Who(ctx), inst.Name, inst.Url);
            await host.ReloadAsync();
            return Results.Ok();
        });

        admin.MapPut("/remote/instances/{id}", async (HttpContext ctx, string id, InstanceUpdateDto dto, ServerConfigHolder cfg, StreamHostService host) =>
        {
            if (cfg.Current.Remote.Instances.All(i => i.Id != id)) return Problem("Instanz nicht gefunden", 404);
            cfg.Update(c =>
            {
                var i = c.Remote.Instances.First(x => x.Id == id);
                i.Enabled = dto.Enabled;
                i.Devices = [.. dto.Devices.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct()];
            });
            log.LogInformation("{Admin} updated instance {Id}: {Count} stick(s) shared", Who(ctx), id, dto.Devices.Count);
            await host.ReloadAsync();
            return Results.Ok();
        });

        admin.MapDelete("/remote/instances/{id}", async (HttpContext ctx, string id, ServerConfigHolder cfg, StreamHostService host) =>
        {
            var inst = cfg.Current.Remote.Instances.FirstOrDefault(i => i.Id == id);
            if (inst is null) return Problem("Instanz nicht gefunden", 404);
            cfg.Update(c => c.Remote.Instances.RemoveAll(i => i.Id == id));
            host.Remote.ForgetToken(id);
            log.LogInformation("{Admin} removed instance {Name}", Who(ctx), inst.Name);
            await host.ReloadAsync();
            return Results.Ok();
        });

        admin.MapGet("/settings/logging", (ServerConfigHolder cfg) => cfg.Current.Logging);

        admin.MapPut("/settings/logging", (LogConfig dto, ServerConfigHolder cfg, FileLoggerProvider log) =>
        {
            if (dto.RetentionDays is < 1 or > 365) return Problem("Aufbewahrung: 1–365 Tage");
            cfg.Update(c => c.Logging = new LogConfig { RetentionDays = dto.RetentionDays, Debug = dto.Debug });
            log.DeleteOldFiles();
            return Results.Ok();
        });

        admin.MapGet("/logs", (FileLoggerProvider log) =>
            new DirectoryInfo(log.Directory).EnumerateFiles(FileLoggerProvider.FilePrefix + "*.log")
                .Where(f => FileLoggerProvider.FileNamePattern().IsMatch(f.Name))
                .OrderByDescending(f => f.Name)
                .Select(f => new { name = f.Name, size = CurrentLength(f) })
                .ToList());

        // Last lines of one log file (tail), or the whole file as download.
        admin.MapGet("/logs/{name}", (string name, int? tail, bool? download, FileLoggerProvider log) =>
        {
            if (!FileLoggerProvider.FileNamePattern().IsMatch(name)) return Problem("Ungültiger Dateiname");
            var path = Path.Combine(log.Directory, name);
            if (!File.Exists(path)) return Problem("Datei nicht gefunden", 404);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (download == true) return Results.File(stream, "text/plain; charset=utf-8", name);
            using (stream) return Results.Text(Tail(stream, Math.Clamp(tail ?? 500, 1, 5000)), "text/plain; charset=utf-8");
        });

        admin.MapPut("/admin/password", (HttpContext ctx, PasswordChangeDto dto, UserStore users) =>
        {
            var name = ctx.User.Identity!.Name!;
            if (!users.VerifyAdmin(name, dto.Current ?? "")) return Problem("Aktuelles Passwort falsch", 403);
            if (UserStore.ValidatePassword(dto.New) is { } e) return Problem(e);
            if (dto.New == dto.Current || dto.New == UserStore.DefaultPassword) return Problem("Bitte ein neues Passwort wählen");
            users.SetAdmin(name, dto.New);
            log.LogInformation("{Admin} changed the admin password", Who(ctx));
            return Results.Ok();
        });
    }

    /// <summary>Real size of a file that may be open for writing (directory metadata lags behind on Windows).</summary>
    private static long CurrentLength(FileInfo f)
    {
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return s.Length;
        }
        catch (IOException) { return f.Length; }
    }

    /// <summary>Last <paramref name="lines"/> lines; reads at most the last 1 MiB of the file.</summary>
    private static string Tail(FileStream stream, int lines)
    {
        const int maxBytes = 1 << 20;
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var text = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
        var all = text.Split('\n');
        if (start > 0) all = all[1..]; // first line is probably cut
        return string.Join('\n', all.TakeLast(lines + 1)).TrimEnd();
    }

    /// <summary>Client address with IPv4-mapped IPv6 (::ffff:a.b.c.d) shown as plain IPv4.</summary>
    private static IPAddress ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string OriginText(RtlTcp.Core.Protocol.RemoteOrigin? o) =>
        o is null ? "entfernt" : $"entfernt: {o.Site ?? "?"}{(o.Instance is { } i ? $" über {i}" : "")}";

    private static IResult Problem(string message, int status = 400) =>
        Results.Json(new { error = message }, statusCode: status);

    private static Task SignInAsync(HttpContext ctx, string user) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });

    private static readonly IPNetwork[] LocalNetworks =
        [.. new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7", "fe80::/10" }
            .Select(IPNetwork.Parse)];

    /// <summary>Private network (the default password only works from there).</summary>
    private static bool IsLocal(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
            ip = new IPAddress(ip.GetAddressBytes()); // drop %scope for matching
        return LocalNetworks.Any(n => n.Contains(ip));
    }

    private static string? ValidateDevices(List<DeviceConfig> devices, IReadOnlySet<int> webPorts)
    {
        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) return "Jedes Gerät braucht einen Namen";
            if (d.Location.Length > 100 || d.Antenna.Length > 100) return $"{d.Name}: Standort und Antenne höchstens 100 Zeichen";
            if (d.Port is < 1 or > 65535) return $"{d.Name}: ungültiger Port {d.Port}";
            if (webPorts.Contains(d.Port)) return $"{d.Name}: Port {d.Port} wird von der Weboberfläche verwendet";
            if (!IPAddress.TryParse(d.Bind, out _)) return $"{d.Name}: ungültige Adresse {d.Bind}";
            if (d.IdleTimeoutSeconds < 0) return $"{d.Name}: Leerlaufzeit darf nicht negativ sein";
            if (d.V1WaitForDeviceSeconds is < 0 or > 300) return $"{d.Name}: Wartezeit 0–300 s";
            if (!Rtl2832.IsValidSampleRate(d.Defaults.SampleRate)) return $"{d.Name}: ungültige Samplerate {d.Defaults.SampleRate}";
            if (d.Defaults.Frequency is < 24_000_000 or > 1_766_000_000) return $"{d.Name}: Frequenz außerhalb 24–1766 MHz";
            if (d.Defaults.Ppm is < -1000 or > 1000) return $"{d.Name}: ppm außerhalb ±1000";
        }
        var dupPort = devices.Where(d => d.Enabled).GroupBy(d => d.Port).FirstOrDefault(g => g.Count() > 1);
        if (dupPort is not null) return $"Port {dupPort.Key} ist mehrfach vergeben";
        foreach (var d in devices)
        {
            if (d.Remote is not { } r) continue;
            if (r.Instance.Length == 0)
                return $"{d.Name}: Direktverbindung (Testbetrieb) gibt es nicht mehr – Gerät entfernen und über „+ Entfernter Stick“ aus einer Instanz neu anlegen";
            if (!r.Share.Contains(':')) return $"{d.Name}: bitte einen freigegebenen Stick der Instanz auswählen";
        }
        var usb = devices.Where(d => d.Remote is null).ToList();
        var dupSerial = usb.Where(d => d.Serial.Length > 0).GroupBy(d => d.Serial).FirstOrDefault(g => g.Count() > 1);
        if (dupSerial is not null) return $"Stick {dupSerial.Key} ist mehrfach eingetragen";
        if (usb.Count(d => d.Enabled && d.Serial.Length == 0) > 1) return "Nur ein Gerät darf ohne Seriennummer sein";
        return null;
    }
}

/// <summary>Blocks an IP for 10 minutes after 10 failed logins within 10 minutes.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTimeOffset First, DateTimeOffset? BlockedUntil)> _state = new();

    public bool IsBlocked(IPAddress ip, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_state.TryGetValue(ip, out var s) || s.BlockedUntil is not { } until) return false;
        remaining = until - DateTimeOffset.Now;
        if (remaining > TimeSpan.Zero) return true;
        _state.TryRemove(ip, out _);
        return false;
    }

    public void Failed(IPAddress ip) => _state.AddOrUpdate(ip,
        _ => (1, DateTimeOffset.Now, null),
        (_, s) =>
        {
            if (DateTimeOffset.Now - s.First > Window) return (1, DateTimeOffset.Now, null);
            var count = s.Count + 1;
            return (count, s.First, count >= MaxFailures ? DateTimeOffset.Now + Window : null);
        });

    public void Succeeded(IPAddress ip) => _state.TryRemove(ip, out _);
}
