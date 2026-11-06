// rtltcp-tray.exe – tray icon for the rtltcp.server Windows service.
//   rtltcp-tray                      show the tray icon (started at logon)
//   rtltcp-tray --service <action>   start | stop | restart the service (run elevated by the tray via UAC)

namespace RtlTcp.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) =>
        args is ["--service", var action] ? ServiceControl.Run(action) : TrayApp.Run();
}
