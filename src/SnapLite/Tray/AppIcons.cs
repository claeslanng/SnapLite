using System.Drawing.Drawing2D;
using SnapLite.Interop;

namespace SnapLite.Tray;

/// <summary>Draws the tray icon at runtime (no .ico resources to load or keep in memory).</summary>
internal static class AppIcons
{
    public static Icon Create(bool paused, int? pixelSize = null)
    {
        int size = pixelSize ?? Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = DrawBitmap(size, paused);
        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { Native.DestroyIcon(hIcon); }
    }

    /// <summary>The same picture as a transparent bitmap (also used for the browser extension's icons).</summary>
    public static Bitmap DrawBitmap(int size, bool paused = false)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float s = size;
            var body = new RectangleF(s * 0.04f, s * 0.04f, s * 0.92f, s * 0.92f);
            using (var fill = new SolidBrush(paused ? Color.FromArgb(120, 120, 120) : Color.FromArgb(0, 120, 212)))
            using (var path = RoundedRect(body, s * 0.22f))
                g.FillPath(fill, path);

            // Viewfinder corner brackets.
            using var pen = new Pen(Color.White, Math.Max(1.5f, s / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float a = s * 0.26f, b = s * 0.74f, l = s * 0.16f;
            g.DrawLines(pen, [new PointF(a, a + l), new PointF(a, a), new PointF(a + l, a)]);
            g.DrawLines(pen, [new PointF(b - l, a), new PointF(b, a), new PointF(b, a + l)]);
            g.DrawLines(pen, [new PointF(a, b - l), new PointF(a, b), new PointF(a + l, b)]);
            g.DrawLines(pen, [new PointF(b - l, b), new PointF(b, b), new PointF(b, b - l)]);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
