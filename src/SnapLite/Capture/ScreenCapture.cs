using System.Drawing.Imaging;

namespace SnapLite.Capture;

/// <summary>
/// Screen grabs via BitBlt (Graphics.CopyFromScreen). The process is PerMonitorV2-aware,
/// so every rectangle here is in physical pixels and captures are 1:1 on scaled displays.
/// </summary>
internal static class ScreenCapture
{
    public static Bitmap Capture(Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0)
            throw new ArgumentException("Capture area is empty.", nameof(area));

        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        try
        {
            bmp.SetResolution(96, 96); // keep 1 image pixel = 1 screen pixel in viewers that honour DPI
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    public static Bitmap CaptureAllMonitors() => Capture(SystemInformation.VirtualScreen);

    public static Bitmap CaptureCurrentMonitor() => Capture(Screen.FromPoint(Cursor.Position).Bounds);
}
