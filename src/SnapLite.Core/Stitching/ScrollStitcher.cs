using SnapLite.Core.Imaging;

namespace SnapLite.Core.Stitching;

public enum StitchStatus
{
    /// <summary>The first frame was stored.</summary>
    First,
    /// <summary>New rows were appended.</summary>
    Appended,
    /// <summary>The window didn't scroll (end of the page, or it hasn't moved yet).</summary>
    NoChange,
    /// <summary>The frame couldn't be aligned with the previous one; stitching should stop.</summary>
    NoMatch,
    /// <summary>The maximum height was reached; the result is truncated.</summary>
    MaxHeightReached,
}

public readonly record struct StitchResult(StitchStatus Status, int Dy = 0);

/// <summary>
/// Builds one tall image from screenshots of a window taken while it scrolls down.
///
/// <para>On the first pair of frames that differ it works out:</para>
/// <list type="bullet">
/// <item>which columns change (used for matching),</item>
/// <item>how tall the sticky header and footer are (rows identical at the same position).
/// The header is re-checked on every step, because many sites restyle their nav bar once you
/// scroll away from the top.</item>
/// </list>
/// <para>For each frame it finds the scroll offset by strip-hash matching (cells that stay in
/// place, like sticky boxes, are ignored) and appends only the newly revealed rows, full width.</para>
/// <para>Which columns end up in the image is decided at the end by a majority vote over all
/// steps: columns that moved with the content (drops static side panels, sticky boxes and the
/// scrollbar). The header is kept once at the top and the footer once at the bottom.</para>
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _maxHeight;
    private readonly List<PixelBuffer> _chunks = new(); // full-width rows; cropped to columns in Compose

    private PixelBuffer? _prev;
    private ulong _prevContentHash;
    private RowHashes? _prevHashes;
    private bool _layoutReady;
    private int _hashX0, _hashX1;   // columns used for matching
    private int _top, _bottom;      // body rows [top, bottom); header above, footer below
    private int _lastDy;
    private int[] _columnVotes = [];  // per column: steps in which it moved with the content
    private int[] _columnTests = [];  // per column: steps that could tell (it wasn't one flat colour)
    private int _pairs;

    public ScrollStitcher(int maxHeight = 20000) => _maxHeight = Math.Max(100, maxHeight);

    /// <summary>Height of the image <see cref="Compose"/> would return now.</summary>
    public int Height => !_layoutReady ? _prev?.Height ?? 0 : _chunks.Sum(c => c.Height) + (_prev!.Height - _bottom);

    public bool Truncated { get; private set; }

    /// <summary>The last frame that was accepted (what the next frame is matched against).</summary>
    public PixelBuffer? LastFrame => _prev;
    public int HeaderHeight => _layoutReady ? _top : 0;
    public int FooterHeight => _layoutReady ? _prev!.Height - _bottom : 0;

    /// <summary>Columns that will be kept: those that moved with the content in most steps.</summary>
    public (int X0, int X1) OutputColumns
    {
        get
        {
            if (!_layoutReady) return (0, _prev?.Width ?? 0);
            if (_pairs == 0) return (_hashX0, _hashX1);
            var run = FrameAnalysis.RunAround(OutputMask(), _hashX0, _hashX1);
            // Never return a uselessly narrow image: fall back to the matching band.
            return run.X1 - run.X0 >= Math.Min(200, (_hashX1 - _hashX0) / 2) ? run : (_hashX0, _hashX1);
        }
    }

    /// <summary>
    /// Per column: did it move with the content in at least half of the steps that could tell? Columns
    /// that never showed anything to check (empty margins, a plain scrollbar track) count as moving.
    /// </summary>
    public bool[] OutputMask()
    {
        var ok = new bool[_columnVotes.Length];
        for (int x = 0; x < ok.Length; x++) ok[x] = _columnTests[x] == 0 || _columnVotes[x] * 2 >= _columnTests[x];
        return ok;
    }

    /// <summary>Diagnostics: number of steps that voted on the columns.</summary>
    public int VotePairs => _pairs;

    /// <summary>Diagnostics: the columns that moved with the content in the last appended step.</summary>
    public bool[]? LastMovingColumns { get; private set; }

    /// <summary>Diagnostics: the columns the last step rejected as sticky (detail stayed in place).</summary>
    public bool[]? LastStickyColumns { get; private set; }

    public StitchResult Add(PixelBuffer frame)
    {
        if (_prev == null)
        {
            _prev = frame;
            _prevContentHash = frame.ContentHash();
            _prevHashes = RowHashes.Compute(frame, 0, frame.Width);
            return new StitchResult(StitchStatus.First);
        }
        if (frame.Width != _prev.Width || frame.Height != _prev.Height)
            return new StitchResult(StitchStatus.NoMatch); // window was resized mid-capture

        // Pixel-identical frame (scrollbar included) = the window didn't move: end of the page.
        // Checked first so blank stretches at the bottom of a page are never appended forever.
        ulong contentHash = frame.ContentHash();
        if (contentHash == _prevContentHash) return new StitchResult(StitchStatus.NoChange);

        var result = _layoutReady ? AddNext(frame) : AddSecond(frame);
        if (result.Status is StitchStatus.Appended or StitchStatus.MaxHeightReached) _prevContentHash = contentHash;
        return result;
    }

    private StitchResult AddSecond(PixelBuffer frame)
    {
        var cols = FrameAnalysis.FindChangingColumns(_prev!, frame);
        if (cols == null) return new StitchResult(StitchStatus.NoChange);

        // Don't match on the rightmost strip of the scrolling area: usually the scrollbar.
        int x0 = cols.Value.X0, x1 = cols.Value.X1;
        int trim = Math.Min(24, (x1 - x0) / 10);
        if (x1 - x0 - trim >= 32) x1 -= trim;

        var ha = RowHashes.Compute(_prev!, x0, x1);
        var hb = RowHashes.Compute(frame, x0, x1);
        int h = frame.Height;
        int header = Math.Min(FrameAnalysis.CountEqualPrefix(ha.Row, hb.Row), (int)(h * 0.4));
        int footer = Math.Min(FrameAnalysis.CountEqualSuffix(ha.Row, hb.Row), (int)(h * 0.3));
        if (h - header - footer < h / 4) { header = Math.Min(header, h / 4); footer = Math.Min(footer, h / 4); }
        if (header + footer >= h) return new StitchResult(StitchStatus.NoChange);

        int top = header, bottom = h - footer;
        var (dy, skip) = FrameAnalysis.FindOffsetDetailed(ha, hb, top, bottom);
        if (dy == null) return new StitchResult(StitchStatus.NoMatch);
        if (dy == 0) return new StitchResult(StitchStatus.NoChange);

        _hashX0 = x0; _hashX1 = x1;
        _top = GrowHeader(top, skip, bottom, h); _bottom = bottom;
        _columnVotes = new int[frame.Width];
        _columnTests = new int[frame.Width];
        _layoutReady = true;

        // First frame: header + body (the footer is added once, from the last frame, at the end).
        _chunks.Add(_prev!.Crop(0, frame.Width, 0, Math.Min(_bottom, _maxHeight)));
        if (_bottom > _maxHeight)
        {
            Truncated = true;
            return new StitchResult(StitchStatus.MaxHeightReached);
        }
        return Append(frame, hb, dy.Value);
    }

    private StitchResult AddNext(PixelBuffer frame)
    {
        var hb = RowHashes.Compute(frame, _hashX0, _hashX1);

        // Re-check the sticky header: a nav bar often changes look (shadow, background) after the
        // first scroll, so it only becomes "identical in place" from the second step on.
        int header = Math.Min(FrameAnalysis.CountEqualPrefix(_prevHashes!.Row, hb.Row), (int)(frame.Height * 0.4));
        if (header > _top && _bottom - header >= frame.Height / 4)
        {
            _top = header;
            // Column votes taken while the nav bar was still treated as content are unreliable.
            Array.Clear(_columnVotes);
            Array.Clear(_columnTests);
            _pairs = 0;
        }

        var (dy, skip) = FrameAnalysis.FindOffsetDetailed(_prevHashes!, hb, _top, _bottom, _lastDy);
        if (dy == null) return new StitchResult(StitchStatus.NoMatch);
        if (dy == 0) return new StitchResult(StitchStatus.NoChange);

        int grown = GrowHeader(_top, skip, _bottom, frame.Height);
        if (grown != _top)
        {
            _top = grown;
            Array.Clear(_columnVotes);
            Array.Clear(_columnTests);
            _pairs = 0;
        }
        return Append(frame, hb, dy.Value);
    }

    /// <summary>
    /// A see-through sticky nav bar never stays identical, so it's found as a band of rows right below
    /// the header that didn't move with the content; from then on it counts as header.
    /// </summary>
    private static int GrowHeader(int top, int skip, int bottom, int height)
    {
        int grown = top + skip;
        return grown <= height * 0.4 && bottom - grown >= height / 4 ? grown : top;
    }

    private StitchResult Append(PixelBuffer frame, RowHashes hashes, int dy)
    {
        // Vote on which columns moved with the content in this step.
        var sticky = new bool[frame.Width];
        var testable = new bool[frame.Width];
        var moving = FrameAnalysis.ColumnsMovingBy(_prev!, frame, dy, _top, _bottom, 0.99, sticky, testable);
        for (int x = 0; x < moving.Length; x++)
        {
            if (!testable[x]) continue;
            _columnTests[x]++;
            if (moving[x]) _columnVotes[x]++;
        }
        _pairs++;
        LastMovingColumns = moving;
        LastStickyColumns = sticky;

        _lastDy = dy;
        int footer = frame.Height - _bottom;
        int room = _maxHeight - _chunks.Sum(c => c.Height) - footer;
        int rows = Math.Min(dy, Math.Max(0, room));
        if (rows > 0) _chunks.Add(frame.Crop(0, frame.Width, _bottom - dy, rows));
        _prev = frame;
        _prevHashes = hashes;
        if (rows < dy)
        {
            Truncated = true;
            return new StitchResult(StitchStatus.MaxHeightReached, rows);
        }
        return new StitchResult(StitchStatus.Appended, dy);
    }

    /// <summary>The stitched image so far: header, all body rows, then the footer of the last frame.</summary>
    public PixelBuffer Compose()
    {
        if (_prev == null) throw new InvalidOperationException("No frames were added.");
        if (!_layoutReady) return _prev; // never scrolled: just the single frame

        var (ox0, ox1) = OutputColumns;
        var parts = new List<PixelBuffer>(_chunks);
        int footer = _prev.Height - _bottom;
        if (footer > 0) parts.Add(_prev.Crop(0, _prev.Width, _bottom, footer));

        var result = new PixelBuffer(ox1 - ox0, parts.Sum(p => p.Height));
        int y = 0;
        foreach (var p in parts)
            for (int r = 0; r < p.Height; r++, y++)
                p.Row(r).Slice(ox0, ox1 - ox0).CopyTo(result.Row(y));
        return result;
    }
}
