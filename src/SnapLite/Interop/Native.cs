using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SnapLite.Interop;

/// <summary>All Win32 P/Invoke declarations in one place.</summary>
internal static unsafe partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_GETDPISCALEDSIZE = 0x02E4;
    public const int WM_APP = 0x8000;
    public const int WM_SNAPLITE_SHOW = WM_APP + 1;

    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public const uint ASFW_ANY = unchecked((uint)-1);

    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x1,
        Control = 0x2,
        Shift = 0x4,
        Win = 0x8,
        NoRepeat = 0x4000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    // ---- Hotkeys / messaging -------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, HotkeyModifiers fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    // ---- Window enumeration ---------------------------------------------------

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetTopWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

    public static string GetClassName(IntPtr hWnd)
    {
        char* buf = stackalloc char[256];
        int n = GetClassNameW(hWnd, buf, 256);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static string GetWindowText(IntPtr hWnd)
    {
        char* buf = stackalloc char[512];
        int n = GetWindowTextW(hWnd, buf, 512);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    // ---- DWM ------------------------------------------------------------------

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ---- Misc -----------------------------------------------------------------

    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    public static bool IsWinKeyDown() => (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

    // ---- Scroll capture -----------------------------------------------------

    public const uint VK_ESCAPE = 0x1B;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const int WHEEL_DELTA = 120;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>INPUT with only the mouse member (the largest one, so the size matches the native union).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>Sends mouse-wheel notches to whatever window is under the cursor (negative = scroll down).</summary>
    public static void MouseWheel(int notches)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches * WHEEL_DELTA)), dwFlags = MOUSEEVENTF_WHEEL },
        };
        SendInput(1, &input, sizeof(INPUT));
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    // ---- Toast ---------------------------------------------------------------

    public const int CS_DROPSHADOW = 0x00020000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public const uint PW_RENDERFULLCONTENT = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI of the monitor containing <paramref name="p"/> (96 if unknown).</summary>
    public static int DpiAt(Point p)
    {
        IntPtr mon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? (int)dpi : 96;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);
}
