namespace RtlTcp.Server;

/// <summary>Where config.json, users.json (web admins) and remote/ (certificate, tokens) live.</summary>
public static class DataDirectory
{
    /// <summary>
    /// Order: <c>--data &lt;dir&gt;</c>, env <c>RTLTCP_DATA</c>, then per OS:
    /// Windows %ProgramData%\rtltcp.server, Linux /var/lib/rtltcp-server (if writable) else ~/.config/rtltcp-server.
    /// </summary>
    public static string Resolve(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        if (i >= 0 && i + 1 < args.Length) return Ensure(args[i + 1]);
        if (Environment.GetEnvironmentVariable("RTLTCP_DATA") is { Length: > 0 } env) return Ensure(env);

        if (OperatingSystem.IsWindows())
            return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rtltcp.server"));

        const string system = "/var/lib/rtltcp-server";
        try
        {
            return Ensure(system);
        }
        catch (UnauthorizedAccessException)
        {
            return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rtltcp-server"));
        }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, ".write-test");
        File.WriteAllText(probe, "");
        File.Delete(probe);
        return Path.GetFullPath(dir);
    }
}
