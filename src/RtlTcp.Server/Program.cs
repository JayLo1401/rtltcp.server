using RtlTcp.Server;
using RtlTcp.Server.Logging;
using RtlTcp.Server.Web;

// CLI:
//   rtltcp-server [--data <dir>]               run the server (web GUI on port 8080 by default)
//   rtltcp-server --version                    print the version
//   rtltcp-server user add <name> <password>   create/update a stream user
if (args is ["--version" or "-v", ..])
{
    Console.WriteLine($"rtltcp.server {StreamHostService.Version}");
    return;
}

var dataDir = DataDirectory.Resolve(args);

if (args is ["user", "add", var name, var password, ..])
{
    new UserStore(dataDir).SetPassword(name, password);
    Console.WriteLine($"Stream user '{name}' saved in {Path.Combine(dataDir, "users.json")}");
    return;
}

if (args is ["admin", "reset", ..])
{
    new UserStore(dataDir).ResetAdmin();
    Console.WriteLine("Web login reset to admin / admin (password change required at the next login).");
    return;
}

if (args.Length > 0 && args[0] == "setup")
{
    Environment.Exit(SetupCommand.Run(args, dataDir));
}

var config = new ServerConfigHolder(dataDir);
var users = new UserStore(dataDir);
var defaultAdminCreated = users.EnsureDefaultAdmin();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "rtltcp.server");
builder.Services.AddSystemd();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
// Debug messages only reach the log files (when enabled in the web GUI), never the console/journal.
var fileLog = new FileLoggerProvider(Path.Combine(dataDir, "logs"),
    () => config.Current.Logging.RetentionDays, () => config.Current.Logging.Debug);
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Information);
builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.Logging.AddProvider(fileLog);
builder.Services.AddSingleton(fileLog);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(users);
builder.Services.AddSingleton<StreamHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamHostService>());
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.AddWeb(config);

var app = builder.Build();
app.UseWeb();
app.Logger.LogInformation("Web GUI on http://{Bind}:{Port}/{Ingress}", config.Current.Web.Bind, WebSetup.WebPort(config),
    WebSetup.IngressEnabled ? " (Home Assistant ingress enabled)" : "");
if (defaultAdminCreated)
    app.Logger.LogWarning("First start: web login is admin / admin - the password must be changed at the first login");
await app.RunAsync();
