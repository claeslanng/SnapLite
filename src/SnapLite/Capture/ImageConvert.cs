using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SnapLite.Core.Imaging;

namespace SnapLite.Capture;

/// <summary>Copies between GDI+ bitmaps and the platform-neutral <see cref="PixelBuffer"/>.</summary>
internal static class ImageConvert
{
    public static PixelBuffer ToPixelBuffer(Bitmap bmp)
    {
        var buf = new PixelBuffer(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buf.Data, y * buf.Stride, buf.Stride);
        }
        finally { bmp.UnlockBits(data); }

        // The 4th byte of 32bppRgb is undefined; make it constant so pixel comparisons are exact.
        var bytes = buf.Data;
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 0xFF;
        return buf;
    }

    /// <summary>
    /// Loads an image file into an independent 32bpp bitmap (GDI+ would otherwise keep the file locked
    /// for the bitmap's lifetime), at 96 DPI like every other capture.
    /// </summary>
    public static Bitmap LoadFile(string path)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var tmp = new Bitmap(ms);
        var copy = tmp.Clone(new Rectangle(0, 0, tmp.Width, tmp.Height), PixelFormat.Format32bppRgb);
        copy.SetResolution(96, 96);
        return copy;
    }

    public static Bitmap ToBitmap(PixelBuffer buf)
    {
        var bmp = new Bitmap(buf.Width, buf.Height, PixelFormat.Format32bppRgb);
        bmp.SetResolution(96, 96);
        var data = bmp.LockBits(new Rectangle(0, 0, buf.Width, buf.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < buf.Height; y++)
                Marshal.Copy(buf.Data, y * buf.Stride, data.Scan0 + y * data.Stride, buf.Stride);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
