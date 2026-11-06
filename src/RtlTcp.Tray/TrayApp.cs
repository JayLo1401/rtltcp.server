using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using static RtlTcp.Tray.Native;

namespace RtlTcp.Tray;

/// <summary>
/// Tray icon with a hidden window and a Win32 message loop. A background thread polls the service every
/// few seconds and posts the result to the window. Updates: the installer renames the running exe and puts
/// the new one in place; the tray notices the new version and restarts itself from it.
/// </summary>
internal static unsafe class TrayApp
{
    private const uint WM_TRAY = WM_APP + 1, WM_REFRESH = WM_APP + 2, WM_RELAUNCH = WM_APP + 3;
    private const int CmdOpen = 1, CmdRestart = 2, CmdStop = 3, CmdStart = 4, CmdClose = 9;

    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "rtltcp-tray.exe");

    private static nint _hwnd, _iconOk, _iconOff, _iconWarn;
    private static uint _taskbarCreated;
    private static Snapshot _snapshot = new(null, null);
    private static readonly AutoResetEvent PollNow = new(false);
    private static volatile bool _quit;
    private static bool _relaunch;

    public static int Run()
    {
        using var single = new Mutex(true, @"Local\rtltcp.server.tray", out var first);
        if (!first) return 0;
        DeleteOldFiles();

        var instance = GetModuleHandleW(0);
        fixed (char* className = "rtltcp.server.tray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassExW(&wc) == 0) return 1;
        }
        // Hidden top-level window (not message-only: the context menu needs a foreground window)
        _hwnd = CreateWindowExW(0, "rtltcp.server.tray", "rtltcp.server", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) return 1;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        LoadIcons(instance);
        _snapshot = new Snapshot(ServiceControl.Status(), null);
        AddIcon();

        var poller = new Thread(PollLoop) { IsBackground = true, Name = "poll" };
        poller.Start();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        _quit = true;
        PollNow.Set();
        DeleteIcon();

        if (_relaunch && File.Exists(ExePath))
        {
            single.ReleaseMutex();
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TRAY:
                    switch ((uint)(lParam & 0xFFFF))
                    {
                        case WM_CONTEXTMENU: ShowMenu(); break;
                        case WM_LBUTTONDBLCLK: OpenWeb(); break;
                    }
                    return 0;
                case WM_REFRESH:
                    UpdateIcon(NIM_MODIFY);
                    return 0;
                case WM_RELAUNCH:
                    _relaunch = true;
                    DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return 0;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                AddIcon(); // Explorer restarted: icon is gone, add it again
                return 0;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void ShowMenu()
    {
        var s = _snapshot;
        var running = s.Service == ServiceControllerStatus.Running;
        var stopped = s.Service == ServiceControllerStatus.Stopped;
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdOpen, Texts.OpenWeb);
        SetMenuDefaultItem(menu, CmdOpen, 0);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, running ? MF_STRING : MF_GRAYED, CmdRestart, Texts.Restart);
        if (stopped) AppendMenuW(menu, MF_STRING, CmdStart, Texts.Start);
        else AppendMenuW(menu, running ? MF_STRING : MF_GRAYED, CmdStop, Texts.Stop);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdClose, Texts.CloseTray);

        POINT p;
        GetCursorPos(&p);
        SetForegroundWindow(_hwnd); // otherwise the menu does not close when clicking elsewhere
        var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, _hwnd, 0);
        PostMessageW(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdOpen: OpenWeb(); break;
            case CmdRestart: Control("restart"); break;
            case CmdStop: Control("stop"); break;
            case CmdStart: Control("start"); break;
            case CmdClose: DestroyWindow(_hwnd); break;
        }
    }

    private static void OpenWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ServerStatus.WebUrl()) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no browser registered
        }
    }

    /// <summary>UAC prompt, then the elevated copy does the work; poll fast to show the progress.</summary>
    private static void Control(string action)
    {
        if (!ServiceControl.RunElevated(action)) return;
        PollNow.Set();
    }

    private static void PollLoop()
    {
        using var status = new ServerStatus();
        var ownFile = FileId(ExePath);
        ulong? seenFile = null;
        while (!_quit)
        {
            _snapshot = status.GetAsync().GetAwaiter().GetResult();
            PostMessageW(_hwnd, WM_REFRESH, 0, 0);

            // Installer replaced the exe (update: renamed ours, put a new file there) or removed it (uninstall)?
            // Compared by NTFS file id - the version resource can't be used: for our own path Windows
            // answers from the already loaded image, not from the new file.
            if (!File.Exists(ExePath)) { PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, 0, 0); return; }
            var current = FileId(ExePath);
            if (ownFile is not null && current is not null && current != ownFile)
            {
                // the same new file twice in a row (and openable): the installer is done with it
                if (current == seenFile) { PostMessageW(_hwnd, WM_RELAUNCH, 0, 0); return; }
                seenFile = current;
            }

            // Fast while something is changing, otherwise every 5 s
            var pending = _snapshot.Service is ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending;
            PollNow.WaitOne(pending ? 1000 : 5000);
        }
    }

    /// <summary>NTFS file id (volume + index) of a file, null if it can't be opened right now.</summary>
    private static ulong? FileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            BY_HANDLE_FILE_INFORMATION info;
            if (GetFileInformationByHandle(h, &info) == 0) return null;
            return ((ulong)info.nFileIndexHigh << 32 | info.nFileIndexLow) ^ ((ulong)info.dwVolumeSerialNumber << 48);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Old exe(s) renamed by the installer during an update; deletable once no tray runs from them.</summary>
    private static void DeleteOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "rtltcp-tray.exe.old*"))
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still running elsewhere */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // install folder not readable
        }
    }

    private static void AddIcon()
    {
        UpdateIcon(NIM_ADD);
        var data = NewData();
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private static void DeleteIcon()
    {
        var data = NewData();
        Shell_NotifyIconW(NIM_DELETE, &data);
    }

    private static void UpdateIcon(uint message)
    {
        var s = _snapshot;
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = s.Service != ServiceControllerStatus.Running ? _iconOff : s.HasProblem ? _iconWarn : _iconOk;
        var tip = Texts.Tooltip(s);
        var span = new Span<char>(data.szTip, 128);
        tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(span);
        span[Math.Min(tip.Length, 127)] = '\0';
        if (Shell_NotifyIconW(message, &data) == 0 && message == NIM_MODIFY)
            AddIcon(); // e.g. Explorer was not running at logon yet
    }

    private static NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private static void LoadIcons(nint instance)
    {
        var cx = GetSystemMetrics(SM_CXSMICON);
        var cy = GetSystemMetrics(SM_CYSMICON);
        _iconOk = LoadImageW(instance, APP_ICON_ID, IMAGE_ICON, cx, cy, 0);
        _iconWarn = LoadImageW(0, IDI_WARNING, IMAGE_ICON, cx, cy, LR_SHARED);
        _iconOff = Grayed(_iconOk);
        if (_iconWarn == 0) _iconWarn = _iconOk;
        if (_iconOff == 0) _iconOff = _iconOk;
    }

    /// <summary>Faded grayscale copy of an icon (service stopped).</summary>
    private static nint Grayed(nint icon)
    {
        if (icon == 0) return 0;
        ICONINFO info;
        if (GetIconInfo(icon, &info) == 0) return 0;
        try
        {
            if (info.hbmColor == 0) return 0;
            BITMAP bm;
            GetObjectW(info.hbmColor, sizeof(BITMAP), &bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            var pixels = new uint[w * h];
            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            var dc = GetDC(0);
            try
            {
                fixed (uint* p = pixels)
                    if (GetDIBits(dc, info.hbmColor, 0, (uint)h, p, &bi, 0) == 0) return 0;
            }
            finally
            {
                ReleaseDC(0, dc);
            }
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                uint a = c >> 24, r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                var y = (r * 30 + g * 59 + b * 11) / 100;
                y = y / 2 + 96;  // lighter
                a = a * 3 / 5;   // faded
                pixels[i] = (a << 24) | (y << 16) | (y << 8) | y;
            }
            nint color;
            fixed (uint* p = pixels) color = CreateBitmap(w, h, 1, 32, p);
            var gray = new ICONINFO { fIcon = 1, hbmMask = info.hbmMask, hbmColor = color };
            var result = CreateIconIndirect(&gray);
            DeleteObject(color);
            return result;
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }
}
