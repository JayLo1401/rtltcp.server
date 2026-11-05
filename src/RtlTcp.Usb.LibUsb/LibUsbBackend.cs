using System.Runtime.InteropServices;
using System.Text;
using RtlTcp.Core.Usb;
using static RtlTcp.Usb.LibUsb.Native;

namespace RtlTcp.Usb.LibUsb;

/// <summary><see cref="IUsbBackend"/> on top of libusb-1.0. One context and one event thread per instance.</summary>
public sealed unsafe class LibUsbBackend : IUsbBackend
{
    private readonly IntPtr _ctx;
    private readonly Thread _events;
    private volatile bool _disposed;

    public LibUsbBackend()
    {
        EnsureLoaded();
        Check(libusb_init(out _ctx), "libusb_init");
        _events = new Thread(EventLoop) { IsBackground = true, Name = "libusb-events", Priority = ThreadPriority.AboveNormal };
        _events.Start();
    }

    private void EventLoop()
    {
        var tv = new TimeVal { tv_sec = new CLong(0), tv_usec = new CLong(200_000) };
        while (!_disposed)
            libusb_handle_events_timeout_completed(_ctx, ref tv, null);
    }

    public IReadOnlyList<UsbDeviceInfo> Enumerate(Func<ushort, ushort, bool> filter)
    {
        var result = new List<UsbDeviceInfo>();
        ForEachDevice(filter, (dev, desc, location) =>
        {
            string? serial = null, manufacturer = null, product = null;
            if (libusb_open(dev, out var h) == Success)
            {
                serial = ReadString(h, desc.iSerialNumber);
                manufacturer = ReadString(h, desc.iManufacturer);
                product = ReadString(h, desc.iProduct);
                libusb_close(h);
            }
            result.Add(new UsbDeviceInfo(desc.idVendor, desc.idProduct, serial, manufacturer, product, location));
            return false;
        });
        return result;
    }

    public IUsbDeviceHandle Open(UsbDeviceInfo device)
    {
        IntPtr handle = IntPtr.Zero;
        var rc = ErrorNoDevice;
        ForEachDevice((v, p) => v == device.VendorId && p == device.ProductId, (dev, _, location) =>
        {
            if (location != device.Location) return false;
            rc = libusb_open(dev, out handle);
            return true;
        });
        Check(rc, $"open {device.Location}");
        try
        {
            if (!OperatingSystem.IsWindows()) libusb_set_auto_detach_kernel_driver(handle, 1);
            Check(libusb_claim_interface(handle, 0), "claim interface 0");
        }
        catch
        {
            libusb_close(handle);
            throw;
        }
        return new DeviceHandle(handle);
    }

    private void ForEachDevice(Func<ushort, ushort, bool> filter, Func<IntPtr, DeviceDescriptor, string, bool> action)
    {
        var count = libusb_get_device_list(_ctx, out var list);
        if (count < 0) throw Error((int)count, "get_device_list");
        try
        {
            for (var i = 0; i < count; i++)
            {
                var dev = list[i];
                if (libusb_get_device_descriptor(dev, out var desc) != Success) continue;
                if (!filter(desc.idVendor, desc.idProduct)) continue;
                if (action(dev, desc, Location(dev))) break;
            }
        }
        finally
        {
            libusb_free_device_list(list, 1);
        }
    }

    private static string Location(IntPtr dev)
    {
        var ports = stackalloc byte[8];
        var n = libusb_get_port_numbers(dev, ports, 8);
        var sb = new StringBuilder().Append(libusb_get_bus_number(dev));
        for (var i = 0; i < n; i++) sb.Append(i == 0 ? '-' : '.').Append(ports[i]);
        return sb.ToString();
    }

    private static string? ReadString(IntPtr handle, byte index)
    {
        if (index == 0) return null;
        var buf = stackalloc byte[256];
        var n = libusb_get_string_descriptor_ascii(handle, index, buf, 256);
        return n > 0 ? Encoding.ASCII.GetString(buf, n).Trim() : null;
    }

    internal static UsbException Error(int rc, string what) =>
        new($"{what}: {ErrorName(rc)}", rc) { DeviceGone = rc is ErrorNoDevice or ErrorIo };

    internal static void Check(int rc, string what)
    {
        if (rc < 0) throw Error(rc, what);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.Join(1000);
        libusb_exit(_ctx);
    }

    private sealed class DeviceHandle(IntPtr handle) : IUsbDeviceHandle
    {
        private IntPtr _handle = handle;

        public int ControlIn(byte request, ushort value, ushort index, Span<byte> data, int timeoutMs)
        {
            fixed (byte* p = data)
            {
                var rc = libusb_control_transfer(Handle, 0xC0, request, value, index, p, (ushort)data.Length, (uint)timeoutMs);
                Check(rc, $"control in {value:X4}/{index:X4}");
                return rc;
            }
        }

        public int ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data, int timeoutMs)
        {
            fixed (byte* p = data)
            {
                var rc = libusb_control_transfer(Handle, 0x40, request, value, index, p, (ushort)data.Length, (uint)timeoutMs);
                Check(rc, $"control out {value:X4}/{index:X4}");
                return rc;
            }
        }

        public IBulkStream StartBulkIn(byte endpoint, int bufferSize, int bufferCount, IBulkSink sink)
        {
            // Cancelled transfers of a previous stream can leave the pipe halted (seen on WinUSB) → clear first.
            var rc = libusb_clear_halt(Handle, endpoint);
            if (rc is ErrorNoDevice) throw Error(rc, "clear halt");
            return new BulkStream(Handle, endpoint, bufferSize, bufferCount, sink);
        }

        private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(DeviceHandle));

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h == IntPtr.Zero) return;
            libusb_release_interface(h, 0);
            libusb_close(h);
        }
    }

    /// <summary>Keeps <c>bufferCount</c> async bulk transfers in flight and resubmits them on completion.</summary>
    private sealed class BulkStream : IBulkStream
    {
        private readonly IBulkSink _sink;
        private readonly IntPtr[] _transfers;
        private readonly GCHandle _self;
        private volatile bool _running = true;
        private int _pending;
        private int _errorReported;

        public BulkStream(IntPtr handle, byte endpoint, int bufferSize, int bufferCount, IBulkSink sink)
        {
            _sink = sink;
            _self = GCHandle.Alloc(this);
            _transfers = new IntPtr[bufferCount];
            for (var i = 0; i < bufferCount; i++)
            {
                var t = libusb_alloc_transfer(0);
                var tr = (Transfer*)t;
                tr->dev_handle = handle;
                tr->endpoint = endpoint;
                tr->type = TransferTypeBulk;
                tr->timeout = 0;
                tr->length = bufferSize;
                tr->buffer = (byte*)NativeMemory.AlignedAlloc((nuint)bufferSize, 64);
                tr->callback = (IntPtr)(delegate* unmanaged<Transfer*, void>)&OnComplete;
                tr->user_data = GCHandle.ToIntPtr(_self);
                _transfers[i] = t;
            }
            foreach (var t in _transfers)
            {
                var rc = libusb_submit_transfer(t);
                if (rc < 0)
                {
                    Dispose();
                    throw Error(rc, "submit bulk transfer");
                }
                Interlocked.Increment(ref _pending);
            }
        }

        public bool IsRunning => _running;

        [UnmanagedCallersOnly]
        private static void OnComplete(Transfer* t)
        {
            var self = (BulkStream)GCHandle.FromIntPtr(t->user_data).Target!;
            try
            {
                self.Completed(t);
            }
            catch (Exception ex)
            {
                self.Fail(new UsbException("bulk callback failed: " + ex.Message));
                Interlocked.Decrement(ref self._pending);
            }
        }

        private void Completed(Transfer* t)
        {
            if (t->status == TransferStatus.Completed && _running)
            {
                if (t->actual_length > 0) _sink.OnData(new ReadOnlySpan<byte>(t->buffer, t->actual_length));
                var rc = libusb_submit_transfer((IntPtr)t);
                if (rc == Success) return;
                Fail(Error(rc, "resubmit bulk transfer"));
            }
            else if (t->status is not (TransferStatus.Completed or TransferStatus.Cancelled))
            {
                Fail(new UsbException($"bulk transfer {t->status}", (int)t->status)
                    { DeviceGone = t->status is TransferStatus.NoDevice or TransferStatus.Error });
            }
            Interlocked.Decrement(ref _pending);
        }

        private void Fail(UsbException error)
        {
            _running = false;
            foreach (var t in _transfers) libusb_cancel_transfer(t);
            if (Interlocked.Exchange(ref _errorReported, 1) == 0) _sink.OnError(error);
        }

        /// <summary>Must not be called from <see cref="IBulkSink"/> callbacks (they run on the event thread).</summary>
        public void Dispose()
        {
            _running = false;
            foreach (var t in _transfers)
                if (t != IntPtr.Zero) libusb_cancel_transfer(t);

            var deadline = Environment.TickCount64 + 3000;
            while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 < deadline) Thread.Sleep(5);
            if (Volatile.Read(ref _pending) > 0) return; // libusb still owns the transfers; leaking is safer than freeing

            foreach (var t in _transfers)
            {
                if (t == IntPtr.Zero) continue;
                NativeMemory.AlignedFree(((Transfer*)t)->buffer);
                libusb_free_transfer(t);
            }
            Array.Clear(_transfers);
            if (_self.IsAllocated) _self.Free();
        }
    }
}
