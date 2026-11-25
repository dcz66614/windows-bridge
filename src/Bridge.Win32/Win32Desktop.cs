using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Bridge.Win32;

/// <summary>
/// Raw Win32 surface for the desktop backend (T13/T15/T16/T17).
/// All coordinates are PHYSICAL pixels; the process must run
/// PerMonitorV2 DPI-aware (the agent manifest declares it).
/// Nothing here is called on Linux: the library only compiles
/// there (EnableWindowsTargeting).
/// </summary>
public static class Win32Desktop
{
    // ---- monitors & virtual desktop (T13) ----

    public sealed record MonitorInfo(
        string DeviceName, int Left, int Top, int Width, int Height,
        bool IsPrimary, uint DpiX, uint DpiY);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CMONITORS = 80;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private enum MonitorDpiType { EffectiveDpi = 0, AngularDpi = 1, RawDpi = 2 }

    public static (int Left, int Top, int Width, int Height) VirtualDesktop() =>
        (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
         GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static int MonitorCount() => GetSystemMetrics(SM_CMONITORS);

    public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, _, ref rect, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return true;
                GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi,
                    out var dpiX, out var dpiY);
                monitors.Add(new MonitorInfo(
                    info.szDevice,
                    info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & 1) != 0, dpiX, dpiY));
                return true;
            }, IntPtr.Zero);
        return monitors;
    }

    // ---- session / lock state (T13/T19) ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    /// <summary>True when a user is logged in interactively and the input desktop can be opened.</summary>
    public static bool IsInteractiveSession()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero)
            return false;
        CloseDesktop(desktop);
        return true;
    }

    /// <summary>
    /// The desktop is unlocked when there is a foreground window on
    /// the input desktop. A locked screen has no foreground window.
    /// </summary>
    public static bool IsUnlocked() => GetForegroundWindow() != IntPtr.Zero;

    public static (IntPtr Hwnd, uint ProcessId, string Title) ForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (IntPtr.Zero, 0, string.Empty);
        GetWindowThreadProcessId(hwnd, out var pid);
        var title = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return (hwnd, pid, title.ToString());
    }

    // ---- screenshot (T15) ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// Capture the full virtual desktop as PNG bytes. If the PNG is
    /// larger than <paramref name="maxBytes"/>, the image is halved
    /// (each dimension) until it fits or reaches 1/8 scale; the
    /// returned size is the ACTUAL image size (the crop mapping
    /// uses it, docs/04 §8).
    /// </summary>
    public static (byte[] Png, int Width, int Height) CaptureVirtualDesktopPng(
        int maxBytes = 4 * 1024 * 1024)
    {
        var (left, top, width, height) = VirtualDesktop();
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC failed: no screen device context.");
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    if (!BitBlt(hdc, 0, 0, width, height, screenDc, left, top, SRCCOPY))
                        throw new InvalidOperationException("BitBlt failed.");
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            var scale = 1.0;
            while (true)
            {
                var (png, w, h) = EncodePng(bitmap, scale);
                if (png.Length <= maxBytes || scale <= 0.125)
                    return (png, w, h);
                scale /= 2;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static (byte[] Png, int Width, int Height) EncodePng(Bitmap source, double scale)
    {
        Bitmap image = source;
        var owned = false;
        if (scale < 1.0)
        {
            var w = Math.Max(1, (int)(source.Width * scale));
            var h = Math.Max(1, (int)(source.Height * scale));
            image = new Bitmap(source, new Size(w, h));
            owned = true;
        }
        try
        {
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return (stream.ToArray(), image.Width, image.Height);
        }
        finally
        {
            if (owned)
                image.Dispose();
        }
    }

    // ---- input (T16/T17) ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>
    /// One left click at a native virtual-desktop pixel (T16).
    /// Returns false when SendInput injected fewer events than sent.
    /// </summary>
    public static bool ClickAtNative(int nativeX, int nativeY)
    {
        var (left, top, width, height) = VirtualDesktop();
        var (ax, ay) = Bridge.DesktopCore.CoordinateMapper.NativeToSendInput(
            nativeX, nativeY, left, top, width, height);
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT
                { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT
                { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT
                { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } },
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Type Unicode text via KEYEVENTF_UNICODE (T17). No
    /// clipboard use, no layout dependence. Returns the number of
    /// characters injected (2 events each: down + up).</summary>
    public static int TypeUnicode(string text, CancellationToken ct)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT
                { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT
                { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return (int)(sent / 2);
    }

    /// <summary>Virtual-key code for an allowlisted key name (T17).</summary>
    public static ushort VirtualKeyFor(string key) => key switch
    {
        "CTRL" => 0x11,
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        "WIN" => 0x5B,
        "ENTER" => 0x0D,
        "TAB" => 0x09,
        "ESC" => 0x1B,
        "BACKSPACE" => 0x08,
        "DELETE" => 0x2E,
        "SPACE" => 0x20,
        "HOME" => 0x24,
        "END" => 0x23,
        "LEFT" => 0x25,
        "UP" => 0x26,
        "RIGHT" => 0x27,
        "DOWN" => 0x28,
        "PAGEUP" => 0x21,
        "PAGEDOWN" => 0x22,
        "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
        "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
        "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        _ when key.Length == 1 && key[0] is >= 'A' and <= 'Z' => (ushort)key[0],
        _ when key.Length == 1 && key[0] is >= '0' and <= '9' => (ushort)key[0],
        _ => throw new ArgumentException($"Key '{key}' is not in the allowlist.", nameof(key)),
    };

    /// <summary>
    /// Press and release a short key combination (T17): all keys
    /// down in order, all up in reverse order. The caller tracks
    /// modifiers so a stop can release them (T19).
    /// </summary>
    public static bool PressKeys(IReadOnlyList<string> keys)
    {
        var vks = keys.Select(VirtualKeyFor).ToArray();
        var inputs = new List<INPUT>(vks.Length * 2);
        foreach (var vk in vks)
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        foreach (var vk in vks.Reverse())
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT
                { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } });
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count;
    }

    /// <summary>Release specific virtual keys (T19: stop).</summary>
    public static void ReleaseKeys(IReadOnlyList<ushort> vks)
    {
        if (vks.Count == 0)
            return;
        var inputs = vks.Select(vk => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
