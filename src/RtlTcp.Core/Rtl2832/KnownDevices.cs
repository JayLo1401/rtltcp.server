namespace RtlTcp.Core.Rtl2832;

/// <summary>USB IDs of RTL2832U-based receivers.</summary>
public static class KnownDevices
{
    private static readonly Dictionary<(ushort Vid, ushort Pid), string> Ids = new()
    {
        [(0x0BDA, 0x2832)] = "Generic RTL2832U",
        [(0x0BDA, 0x2838)] = "Generic RTL2832U OEM",
        [(0x0413, 0x6680)] = "DigitalNow Quad DVB-T PCI-E card",
        [(0x0413, 0x6F0F)] = "Leadtek WinFast DTV Dongle mini D",
        [(0x0458, 0x707F)] = "Genius TVGo DVB-T03 USB dongle",
        [(0x0CCD, 0x00A9)] = "Terratec Cinergy T Stick Black (rev 1)",
        [(0x0CCD, 0x00B3)] = "Terratec NOXON DAB/DAB+ USB dongle (rev 1)",
        [(0x0CCD, 0x00D3)] = "Terratec Cinergy T Stick RC (Rev.3)",
        [(0x0CCD, 0x00E0)] = "Terratec NOXON DAB/DAB+ USB dongle (rev 2)",
        [(0x185B, 0x0620)] = "Compro Videomate U620F",
        [(0x185B, 0x0650)] = "Compro Videomate U650F",
        [(0x1F4D, 0xB803)] = "GTek T803",
        [(0x1F4D, 0xC803)] = "Lifeview LV5TDeluxe",
        [(0x1F4D, 0xD286)] = "MyGica TD312",
        [(0x1F4D, 0xD803)] = "PROlectrix DV107669",
    };

    public static bool IsSupported(ushort vid, ushort pid) => Ids.ContainsKey((vid, pid));

    public static string Name(ushort vid, ushort pid) => Ids.GetValueOrDefault((vid, pid), "Unknown RTL2832U");
}
