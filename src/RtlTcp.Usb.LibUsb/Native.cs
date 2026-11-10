using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RtlTcp.Usb.LibUsb;

/// <summary>Minimal P/Invoke declarations for libusb-1.0 (LGPL-2.1, loaded dynamically).</summary>
internal static unsafe partial class Native
{
    private const string Lib = "libusb-1.0";

    static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

    /// <summary>Ensures the static constructor (DLL resolver) has run.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureLoaded() { }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll"),
               Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native", "libusb-1.0.dll")]
            : OperatingSystem.IsMacOS()
                ? ["libusb-1.0.0.dylib", "/opt/homebrew/lib/libusb-1.0.0.dylib", "/usr/local/lib/libusb-1.0.0.dylib"]
                : ["libusb-1.0.so.0", "libusb-1.0.so"];
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }

    public const int Success = 0;
    public const int ErrorIo = -1;
    public const int ErrorAccess = -3;
    public const int ErrorNoDevice = -4;
    public const int ErrorNotFound = -5;
    public const int ErrorBusy = -6;
    public const int ErrorTimeout = -7;
    public const int ErrorPipe = -9;
    public const int ErrorNotSupported = -12;

    public enum TransferStatus
    {
        Completed = 0, Error = 1, TimedOut = 2, Cancelled = 3, Stall = 4, NoDevice = 5, Overflow = 6,
    }

    public const byte TransferTypeBulk = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public byte bLength, bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
        public ushort idVendor, idProduct, bcdDevice;
        public byte iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
    }

    /// <summary>struct libusb_transfer (without the trailing iso packet array).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Transfer
    {
        public IntPtr dev_handle;
        public byte flags;
        public byte endpoint;
        public byte type;
        public uint timeout;
        public TransferStatus status;
        public int length;
        public int actual_length;
        public IntPtr callback;
        public IntPtr user_data;
        public byte* buffer;
        public int num_iso_packets;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TimeVal
    {
        public CLong tv_sec;
        public CLong tv_usec;
    }

    [LibraryImport(Lib)] public static partial int libusb_init(out IntPtr ctx);
    [LibraryImport(Lib)] public static partial void libusb_exit(IntPtr ctx);
    [LibraryImport(Lib)] public static partial nint libusb_get_device_list(IntPtr ctx, out IntPtr* list);
    [LibraryImport(Lib)] public static partial void libusb_free_device_list(IntPtr* list, int unrefDevices);
    [LibraryImport(Lib)] public static partial IntPtr libusb_ref_device(IntPtr dev);
    [LibraryImport(Lib)] public static partial void libusb_unref_device(IntPtr dev);
    [LibraryImport(Lib)] public static partial int libusb_get_device_descriptor(IntPtr dev, out DeviceDescriptor desc);
    [LibraryImport(Lib)] public static partial byte libusb_get_bus_number(IntPtr dev);
    [LibraryImport(Lib)] public static partial int libusb_get_port_numbers(IntPtr dev, byte* numbers, int len);
    [LibraryImport(Lib)] public static partial int libusb_open(IntPtr dev, out IntPtr handle);
    [LibraryImport(Lib)] public static partial void libusb_close(IntPtr handle);
    [LibraryImport(Lib)] public static partial int libusb_get_string_descriptor_ascii(IntPtr handle, byte index, byte* data, int length);
    [LibraryImport(Lib)] public static partial int libusb_set_auto_detach_kernel_driver(IntPtr handle, int enable);
    [LibraryImport(Lib)] public static partial int libusb_claim_interface(IntPtr handle, int iface);
    [LibraryImport(Lib)] public static partial int libusb_release_interface(IntPtr handle, int iface);
    [LibraryImport(Lib)] public static partial int libusb_control_transfer(IntPtr handle, byte requestType, byte request,
        ushort value, ushort index, byte* data, ushort length, uint timeout);
    [LibraryImport(Lib)] public static partial int libusb_clear_halt(IntPtr handle, byte endpoint);
    [LibraryImport(Lib)] public static partial int libusb_reset_device(IntPtr handle);
    [LibraryImport(Lib)] public static partial IntPtr libusb_alloc_transfer(int isoPackets);
    [LibraryImport(Lib)] public static partial void libusb_free_transfer(IntPtr transfer);
    [LibraryImport(Lib)] public static partial int libusb_submit_transfer(IntPtr transfer);
    [LibraryImport(Lib)] public static partial int libusb_cancel_transfer(IntPtr transfer);
    [LibraryImport(Lib)] public static partial int libusb_handle_events_timeout_completed(IntPtr ctx, ref TimeVal tv, int* completed);
    [LibraryImport(Lib)] public static partial IntPtr libusb_error_name(int code);

    public static string ErrorName(int code) => Marshal.PtrToStringAnsi(libusb_error_name(code)) ?? code.ToString();
}
