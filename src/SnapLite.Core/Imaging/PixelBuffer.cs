using System.Runtime.InteropServices;

namespace SnapLite.Core.Imaging;

/// <summary>
/// A plain 32-bit BGRA image (stride = width × 4), independent of GDI+/System.Drawing so the
/// stitching code can be unit-tested anywhere.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int Stride => Width * 4;

    public PixelBuffer(int width, int height, byte[]? data = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must not be empty.");
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 4];
        if (Data.Length < width * height * 4) throw new ArgumentException("Pixel data is too small.", nameof(data));
    }

    public Span<uint> Row(int y) => MemoryMarshal.Cast<byte, uint>(Data.AsSpan(y * Stride, Stride));

    public uint this[int x, int y]
    {
        get => Row(y)[x];
        set => Row(y)[x] = value;
    }

    /// <summary>Copies rows [top, top+count) and columns [x0, x1) into a new buffer.</summary>
    public PixelBuffer Crop(int x0, int x1, int top, int count)
    {
        var dst = new PixelBuffer(x1 - x0, count);
        for (int y = 0; y < count; y++)
            Row(top + y).Slice(x0, x1 - x0).CopyTo(dst.Row(y));
        return dst;
    }

    /// <summary>Fast whole-image hash, used to tell when a scrolling window has settled.</summary>
    public ulong ContentHash()
    {
        ulong h = 14695981039346656037UL;
        foreach (ulong v in MemoryMarshal.Cast<byte, ulong>(Data.AsSpan(0, Width * Height * 4 & ~7)))
            h = (h ^ v) * 1099511628211UL;
        return h;
    }
}
