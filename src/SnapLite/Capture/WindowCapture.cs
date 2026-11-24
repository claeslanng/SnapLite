using SnapLite.Interop;

namespace SnapLite.Capture;

internal static class WindowCapture
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    public readonly record struct Result(Bitmap Image, string Title);

    /// <summary>
    /// Captures the foreground window without its drop shadow. When the foreground is the
    /// taskbar or SnapLite itself (e.g. invoked from the tray menu), the topmost real
    /// application window is used instead.
    /// </summary>
    public static Result? CaptureActiveWindow()
    {
        IntPtr hwnd = FindTargetWindow();
        if (hwnd == IntPtr.Zero) return null;

        Rectangle bounds = GetVisibleBounds(hwnd);
        bounds.Intersect(SystemInformation.VirtualScreen);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new Result(ScreenCapture.Capture(bounds), Native.GetWindowText(hwnd));
    }

    public static IntPtr FindTargetWindow()
    {
        IntPtr fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
        if (IsCandidate(fg)) return fg;

        // Walk the z-order from the top and take the first real app window.
        for (IntPtr h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
        {
            if (IsCandidate(h)) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The window rect as DWM draws it, i.e. without the invisible resize borders and shadow.
    /// In a PerMonitorV2 process these are physical pixels.
    /// </summary>
    public static Rectangle GetVisibleBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttributeRect(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r,
                System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0)
            return r.ToRectangle();

        return Native.GetWindowRect(hwnd, out var wr) ? wr.ToRectangle() : Rectangle.Empty;
    }

    public static bool IsCandidate(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;

        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT)) != 0) return false;

        if (Native.DwmGetWindowAttributeInt(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (ShellClasses.Contains(Native.GetClassName(h))) return false;

        var r = GetVisibleBounds(h);
        return r.Width > 1 && r.Height > 1;
    }
}
