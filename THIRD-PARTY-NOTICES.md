# Third-Party Notices

rtltcp.server itself is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md).
The following third-party components are distributed with it under **their own** licenses.
These licenses are not affected by, and do not extend to, the PolyForm license.

## libusb 1.0.30

- Files: `libusb-1.0.dll` (Windows), on Linux the system library `libusb-1.0.so.0` is used
- License: GNU Lesser General Public License, version 2.1 or later. Full text in
  [`src/RtlTcp.Usb.LibUsb/runtimes/LIBUSB-COPYING.txt`](src/RtlTcp.Usb.LibUsb/runtimes/LIBUSB-COPYING.txt)
- Source: <https://github.com/libusb/libusb/releases/tag/v1.0.30> (unmodified official binaries)
- libusb is loaded dynamically at runtime and ships as a separate file next to the program. You may replace it
  with your own (modified) build of libusb without changing rtltcp.server. Reverse engineering of rtltcp.server for
  debugging such modifications is permitted.

## .NET runtime

Self-contained builds include the .NET runtime (MIT License, © .NET Foundation and Contributors),
<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.
