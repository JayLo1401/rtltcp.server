using RtlTcp.Core.Protocol;
using RtlTcp.Core.Rtl2832;
using RtlTcp.Core.Usb;

namespace RtlTcp.Core.Device;

/// <summary>An opened receiver as seen by the <see cref="DeviceManager"/>.</summary>
public interface ISdrDevice : IDisposable
{
    string Serial { get; }
    TunerType TunerType { get; }
    string TunerName { get; }
    IReadOnlyList<int> Gains { get; }

    /// <summary>Executes a v1 command. Returns false if the command is not supported by this device.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Invalid parameter (e.g. sample rate).</exception>
    bool Execute(Command command);

    IBulkStream StartStream(IBulkSink sink);

    void PowerDown();
}

public interface ISdrDeviceSource
{
    /// <summary>Opens the stick with this serial (empty = first available not in <paramref name="exclude"/>); null if not present.</summary>
    ISdrDevice? TryOpen(string serial, IReadOnlyCollection<string> exclude);

    /// <summary>All sticks currently connected.</summary>
    IReadOnlyList<UsbDeviceInfo> List();

    /// <summary>
    /// True for sticks at another site: never kept open without a client (that would stream over the
    /// internet for nothing), closed as soon as the client leaves.
    /// </summary>
    bool IsRemote => false;

    /// <summary>Why the last <see cref="TryOpen"/> returned null, for the status message; null = generic text.</summary>
    string? LastError => null;
}

/// <summary>Opens real RTL2832U sticks through an <see cref="IUsbBackend"/>.</summary>
public sealed class RtlSdrDeviceSource(IUsbBackend backend) : ISdrDeviceSource
{
    private readonly Lock _lock = new();

    public IReadOnlyList<UsbDeviceInfo> List()
    {
        lock (_lock) return backend.Enumerate(KnownDevices.IsSupported);
    }

    public ISdrDevice? TryOpen(string serial, IReadOnlyCollection<string> exclude)
    {
        lock (_lock)
        {
            var info = backend.Enumerate(KnownDevices.IsSupported).FirstOrDefault(d =>
                d.Serial is not null &&
                (serial.Length > 0 ? d.Serial == serial : !exclude.Contains(d.Serial)));
            if (info is null) return null;
            return new RtlSdrDeviceAdapter(RtlSdrDevice.Open(backend.Open(info)), info.Serial!);
        }
    }

    private sealed class RtlSdrDeviceAdapter(RtlSdrDevice dev, string serial) : ISdrDevice
    {
        public string Serial => serial;
        public TunerType TunerType => dev.TunerType;
        public string TunerName => dev.TunerName;
        public IReadOnlyList<int> Gains => dev.Gains;
        public bool Execute(Command command) => dev.Execute(command);
        public IBulkStream StartStream(IBulkSink sink) => dev.StartStream(sink);
        public void PowerDown() => dev.PowerDown();
        public void Dispose() => dev.Dispose();
    }
}
