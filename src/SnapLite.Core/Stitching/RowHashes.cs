using SnapLite.Core.Imaging;

namespace SnapLite.Core.Stitching;

/// <summary>
/// Per-row hashes of a frame, both for the whole row and for a few vertical strips of it.
/// Matching works on strips so that a floating element that doesn't scroll (chat bubble,
/// "back to top" button, cookie bar) only spoils one strip of the rows it covers instead
/// of making whole rows look different.
/// </summary>
public sealed class RowHashes
{
    public int Height { get; }
    public int Strips { get; }
    /// <summary>Hash of each full row (used to find sticky headers/footers).</summary>
    public ulong[] Row { get; }
    /// <summary>Hash of strip s of row y at [y * Strips + s].</summary>
    public ulong[] Strip { get; }
    /// <summary>True when every pixel in that strip of that row has the same colour.</summary>
    public bool[] StripUniform { get; }

    public RowHashes(int height, int strips)
    {
        Height = height;
        Strips = strips;
        Row = new ulong[height];
        Strip = new ulong[height * strips];
        StripUniform = new bool[height * strips];
    }

    public static RowHashes Compute(PixelBuffer f, int x0, int x1, int maxStrips = 8)
    {
        int width = x1 - x0;
        int strips = Math.Clamp(width / 16, 1, maxStrips);
        var r = new RowHashes(f.Height, strips);
        for (int y = 0; y < f.Height; y++)
        {
            var row = f.Row(y).Slice(x0, width);
            ulong rowHash = 14695981039346656037UL;
            for (int s = 0; s < strips; s++)
            {
                int a = s * width / strips, b = (s + 1) * width / strips;
                uint first = row[a];
                bool same = true;
                ulong h = 14695981039346656037UL;
                for (int x = a; x < b; x++)
                {
                    uint p = row[x];
                    h = (h ^ p) * 1099511628211UL;
                    same &= p == first;
                }
                r.Strip[y * strips + s] = h;
                r.StripUniform[y * strips + s] = same;
                rowHash = (rowHash ^ h) * 1099511628211UL;
            }
            r.Row[y] = rowHash;
        }
        return r;
    }

    /// <summary>Wraps plain row hashes as a single-strip set (used by tests).</summary>
    public static RowHashes FromRows(ulong[] hashes, bool[] uniform)
    {
        var r = new RowHashes(hashes.Length, 1);
        hashes.CopyTo(r.Row, 0);
        hashes.CopyTo(r.Strip, 0);
        uniform.CopyTo(r.StripUniform, 0);
        return r;
    }
}
