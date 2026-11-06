using System.Runtime.InteropServices;

namespace RtlTcp.Tray;

/// <summary>The few Win32 calls the tray icon needs.</summary>
internal static unsafe partial class Native
{
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, WM_APP = 0x8000;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    public const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    public const uint IMAGE_ICON = 1, LR_SHARED = 0x8000;
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    public const nint IDI_WARNING = 32515;
    /// <summary>Icon group id the .NET SDK uses for &lt;ApplicationIcon&gt;.</summary>
    public const nint APP_ICON_ID = 32512;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    public static partial int SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    public static partial int DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(POINT* point);

    [LibraryImport("user32.dll")]
    public static partial nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    public static partial int GetIconInfo(nint icon, ICONINFO* info);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(ICONINFO* info);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME is two DWORDs: 4-byte aligned
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public ulong ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [LibraryImport("kernel32.dll")]
    public static partial int GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, BY_HANDLE_FILE_INFORMATION* info);

    [LibraryImport("shell32.dll")]
    public static partial int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetModuleHandleW(nint name);

    [LibraryImport("kernel32.dll")]
    public static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("gdi32.dll")]
    public static partial int GetObjectW(nint obj, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint obj);
}
