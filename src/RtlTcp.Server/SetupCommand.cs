using System.Net;
using System.Net.Sockets;

namespace RtlTcp.Server;

/// <summary>
/// <c>rtltcp-server setup --web-port N --stream-port N [--check-only] [--data DIR]</c>
/// Used by the Windows installer: checks that the ports are free and stores them in config.json.
/// Exit codes: 0 ok, 1 invalid arguments, 2 web port in use, 3 stream port in use.
/// </summary>
public static class SetupCommand
{
    public static int Run(string[] args, string dataDir)
    {
        int? web = Int(args, "--web-port"), stream = Int(args, "--stream-port");
        var checkOnly = args.Contains("--check-only");
        if (web is not (> 0 and < 65536) || stream is not (> 0 and < 65536) || web == stream)
        {
            Console.Error.WriteLine("usage: rtltcp-server setup --web-port <1-65535> --stream-port <1-65535> [--check-only] [--data <dir>]");
            return 1;
        }
        if (!IsFree(web.Value))
        {
            Console.Error.WriteLine($"Port {web} ist bereits belegt.");
            return 2;
        }
        if (!IsFree(stream.Value))
        {
            Console.Error.WriteLine($"Port {stream} ist bereits belegt.");
            return 3;
        }
        if (checkOnly) return 0;

        var holder = new ServerConfigHolder(dataDir);
        holder.Update(c =>
        {
            c.Web.Port = web.Value;
            c.StreamBasePort = stream.Value;
        });
        Console.WriteLine($"Web-Port {web}, Stream-Port {stream} gespeichert in {holder.ConfigPath}");
        return 0;
    }

    public static bool IsFree(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            s.ExclusiveAddressUse = OperatingSystem.IsWindows();
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            s.Listen(1);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int? Int(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
    }
}
