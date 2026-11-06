using System.ServiceProcess;
using System.Text;

namespace RtlTcp.Tray;

/// <summary>Menu and tooltip texts, German or English depending on the Windows display language.</summary>
internal static class Texts
{
    private static readonly bool German = (Native.GetUserDefaultUILanguage() & 0x3FF) == 0x07;

    private static string T(string de, string en) => German ? de : en;

    public static string OpenWeb => T("Weboberfläche öffnen", "Open web interface");
    public static string Restart => T("Server neu starten", "Restart server");
    public static string Stop => T("Server beenden", "Stop server");
    public static string Start => T("Server starten", "Start server");
    public static string CloseTray => T("Tray-Symbol schließen", "Close tray icon");

    public static string Tooltip(Snapshot s)
    {
        var sb = new StringBuilder("rtltcp.server: ");
        sb.Append(s.Service switch
        {
            null => T("nicht installiert", "not installed"),
            ServiceControllerStatus.Running => T("läuft", "running"),
            ServiceControllerStatus.StartPending => T("startet …", "starting …"),
            ServiceControllerStatus.StopPending => T("wird beendet …", "stopping …"),
            _ => T("beendet", "stopped"),
        });
        foreach (var d in s.Devices ?? [])
        {
            sb.Append('\n').Append(d.Name).Append(": ").Append(d.Message);
            if (d.Client is { } c) sb.Append(" (").Append(c).Append(')');
        }
        // The tooltip holds at most 127 characters
        return sb.Length <= 127 ? sb.ToString() : sb.ToString(0, 126) + "…";
    }
}
