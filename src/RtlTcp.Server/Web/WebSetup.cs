using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

namespace RtlTcp.Server.Web;

public static class WebSetup
{
    /// <summary>Home Assistant Supervisor's ingress proxy. Requests from it are already authenticated by HA.</summary>
    public static readonly IPAddress IngressProxy = IPAddress.Parse("172.30.32.2");

    /// <summary>Set by the Home Assistant add-on (RTLTCP_INGRESS=1).</summary>
    public static bool IngressEnabled { get; } = Environment.GetEnvironmentVariable("RTLTCP_INGRESS") == "1";

    /// <summary>Web port: RTLTCP_WEB_PORT (add-on) overrides config.json.</summary>
    public static int WebPort(ServerConfigHolder config) =>
        int.TryParse(Environment.GetEnvironmentVariable("RTLTCP_WEB_PORT"), out var p) && p is > 0 and < 65536 ? p : config.Current.Web.Port;

    public static bool IsIngress(HttpContext ctx)
    {
        if (!IngressEnabled || ctx.Connection.RemoteIpAddress is not { } ip) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.Equals(IngressProxy);
    }

    public static void AddWeb(this WebApplicationBuilder builder, ServerConfigHolder config)
    {
        var web = config.Current.Web;
        var port = WebPort(config);
        builder.WebHost.ConfigureKestrel(k =>
        {
            var addr = IPAddress.Parse(web.Bind);
            if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)) k.ListenAnyIP(port);
            else k.Listen(addr, port);
        });

        // Cookie encryption keys survive restarts (otherwise every restart logs everybody out).
        var dp = builder.Services.AddDataProtection()
            .SetApplicationName("rtltcp.server")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(config.DataDir, "keys")));
        // Windows: encrypt the cookie keys at rest (machine scope, the service and the CLI share them).
        // Linux: protected by the data directory permissions (only the rtltcp user).
        if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "rtltcp_session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<LoginThrottle>();
    }

    public static void UseWeb(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

            // CSRF: state-changing API calls must carry our header (a cross-site form cannot set it).
            if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method)
                && ctx.Request.Headers[WebApi.CsrfHeader] != "1")
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing " + WebApi.CsrfHeader + " header" });
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
            await next();
        });

        var files = new ManifestEmbeddedFileProvider(typeof(WebSetup).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.UseAuthentication();
        // Home Assistant ingress: HA already authenticated the user, no second login inside the panel.
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true && IsIngress(ctx))
            {
                ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "Home Assistant")], "ingress"));
            }
            await next();
        });
        app.UseAuthorization();
        WebApi.Map(app);
    }
}
