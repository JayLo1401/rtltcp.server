namespace RtlTcp.Core.Usb;

/// <summary>A USB device found during enumeration. <see cref="Serial"/> is null if the device could not be opened.</summary>
public sealed record UsbDeviceInfo(ushort VendorId, ushort ProductId, string? Serial, string? Manufacturer,
    string? Product, string Location);

/// <summary>Platform USB access (libusb on desktop, USB host API on Android).</summary>
public interface IUsbBackend : IDisposable
{
    /// <summary>Lists devices whose VID/PID match <paramref name="filter"/>.</summary>
    IReadOnlyList<UsbDeviceInfo> Enumerate(Func<ushort, ushort, bool> filter);

    IUsbDeviceHandle Open(UsbDeviceInfo device);
}

public interface IUsbDeviceHandle : IDisposable
{
    /// <summary>Vendor control transfer device → host. Returns bytes read.</summary>
    int ControlIn(byte request, ushort value, ushort index, Span<byte> data, int timeoutMs = 300);

    /// <summary>Vendor control transfer host → device. Returns bytes written.</summary>
    int ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data, int timeoutMs = 300);

    /// <summary>Starts continuous bulk-in reads with <paramref name="bufferCount"/> transfers in flight.</summary>
    IBulkStream StartBulkIn(byte endpoint, int bufferSize, int bufferCount, IBulkSink sink);
}

/// <summary>Receives bulk data on the USB event thread. Implementations must be fast and must copy the data.</summary>
public interface IBulkSink
{
    void OnData(ReadOnlySpan<byte> data);

    /// <summary>Called once when the stream stops because of an error (e.g. device unplugged).</summary>
    void OnError(UsbException error);
}

public interface IBulkStream : IDisposable
{
    bool IsRunning { get; }
}

public sealed class UsbException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>True if the device is gone (unplugged, reset) and must be reopened.</summary>
    public bool DeviceGone { get; init; }
}
