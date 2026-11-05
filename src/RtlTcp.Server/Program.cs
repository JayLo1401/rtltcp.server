using RtlTcp.Server;

// CLI:
//   rtltcp-server [--data <dir>]               run the server
//   rtltcp-server --version                    print the version
//   rtltcp-server user add <name> <password>   create/update a stream user (until the web GUI exists)
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

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "rtltcp.server");
builder.Services.AddSystemd();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSingleton(new ServerConfigHolder(dataDir));
builder.Services.AddSingleton(new UserStore(dataDir));
builder.Services.AddSingleton<StreamHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamHostService>());

await builder.Build().RunAsync();
