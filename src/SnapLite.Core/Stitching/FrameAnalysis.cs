using SnapLite.Core.Imaging;

namespace SnapLite.Core.Stitching;

/// <summary>Pure functions that compare two screenshots of a scrolling window.</summary>
public static class FrameAnalysis
{
    /// <summary>
    /// Hashes every row over columns [x0, x1). Rows whose pixels are all the same colour are flagged
    /// as uniform: they match anything and must not be used as evidence when aligning frames.
    /// </summary>
    public static void HashRows(PixelBuffer f, int x0, int x1, ulong[] hashes, bool[] uniform)
    {
        for (int y = 0; y < f.Height; y++)
        {
            var row = f.Row(y).Slice(x0, x1 - x0);
            uint first = row[0];
            bool same = true;
            ulong h = 14695981039346656037UL;
            foreach (uint p in row)
            {
                h = (h ^ p) * 1099511628211UL;
                same &= p == first;
            }
            hashes[y] = h;
            uniform[y] = same;
        }
    }

    /// <summary>
    /// Finds the columns that scroll: the widest run of columns where at least
    /// <paramref name="minRowFraction"/> of the rows changed between the two frames
    /// (static sidebars, toolbars and a blinking caret are left out). Null if nothing changed.
    /// </summary>
    public static (int X0, int X1)? FindChangingColumns(PixelBuffer a, PixelBuffer b, double minRowFraction = 0.05, int maxGap = 16)
    {
        int w = a.Width, h = a.Height;
        var changed = new int[w];
        for (int y = 0; y < h; y++)
        {
            var ra = a.Row(y);
            var rb = b.Row(y);
            for (int x = 0; x < w; x++)
                if (ra[x] != rb[x]) changed[x]++;
        }
        int threshold = Math.Max(1, (int)(h * minRowFraction));
        return LongestRun(changed.Select(c => c >= threshold).ToArray(), maxGap);
    }

    /// <summary>
    /// Share of the frame that differs between two screenshots, measured in cells of
    /// 16 strips × 1 row. A blinking text cursor or a small animation changes well under 1%;
    /// a real scroll changes most of the window. Used to decide "settled" and "didn't move"
    /// without requiring pixel-identical frames.
    /// </summary>
    public static double ChangedFraction(PixelBuffer a, PixelBuffer b, int strips = 16)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1;
        int w = a.Width;
        strips = Math.Clamp(w / 8, 1, strips);
        int changed = 0;
        for (int y = 0; y < a.Height; y++)
        {
            var ra = a.Row(y);
            var rb = b.Row(y);
            if (ra.SequenceEqual(rb)) continue;
            for (int s = 0; s < strips; s++)
            {
                int x0 = s * w / strips, x1 = (s + 1) * w / strips;
                if (!ra[x0..x1].SequenceEqual(rb[x0..x1])) changed++;
            }
        }
        return changed / (double)(a.Height * strips);
    }

    /// <summary>Number of leading rows that are identical at the same position (a sticky header).</summary>
    public static int CountEqualPrefix(ulong[] a, ulong[] b)
    {
        int n = 0;
        while (n < a.Length && a[n] == b[n]) n++;
        return n;
    }

    /// <summary>Number of trailing rows that are identical at the same position (a sticky footer / status bar).</summary>
    public static int CountEqualSuffix(ulong[] a, ulong[] b)
    {
        int n = 0;
        while (n < a.Length && a[^(n + 1)] == b[^(n + 1)]) n++;
        return n;
    }

    /// <summary>
    /// Finds how far the content moved up from frame A to frame B, looking only at body rows
    /// [top, bottom). Row y of B is expected to equal row y + dy of A.
    /// Distinctive (non-uniform) rows vote for an offset through a hash index of A. Each offset is
    /// scored by votes per overlapping row (so small offsets don't win just by overlapping more);
    /// near-ties, typical for repetitive lists, go to the offset closest to <paramref name="expected"/>.
    /// The winner is verified over the whole overlap.
    /// If B's body is completely blank there is nothing to align on; then <paramref name="expected"/>
    /// is used when it is consistent. Returns 0 if nothing moved, null if no convincing match.
    /// </summary>
    public static int? FindOffset(ulong[] ha, bool[] ua, ulong[] hb, bool[] ub, int top, int bottom, int? expected = null,
        double minMatch = 0.85, int minOverlap = 16) =>
        FindOffset(RowHashes.FromRows(ha, ua), RowHashes.FromRows(hb, ub), top, bottom, expected, minMatch, minOverlap);

    /// <summary>
    /// Strip-based version: each (row, strip) cell votes and is verified on its own, so content that
    /// doesn't scroll in part of the width (a floating chat bubble, a "back to top" button) only
    /// lowers the match a little instead of making every row it touches fail.
    /// </summary>
    public static int? FindOffset(RowHashes a, RowHashes b, int top, int bottom, int? expected = null,
        double minMatch = 0.85, int minOverlap = 16) =>
        FindOffsetDetailed(a, b, top, bottom, expected, minMatch, minOverlap).Dy;

    /// <summary>
    /// Like <see cref="FindOffset(RowHashes, RowHashes, int, int, int?, double, int)"/>, and also reports
    /// <c>SkipTop</c>: how many rows right below <paramref name="top"/> neither stayed put nor moved
    /// with the content. That is the signature of a semi-transparent sticky nav bar (the page shows
    /// through it, blurred), which the caller should treat as part of the header from now on.
    /// </summary>
    public static (int? Dy, int SkipTop) FindOffsetDetailed(RowHashes a, RowHashes b, int top, int bottom, int? expected = null,
        double minMatch = 0.85, int minOverlap = 16)
    {
        int k = a.Strips;
        int body = bottom - top;
        int minUsefulOverlap = Math.Max(minOverlap, (int)(body * 0.15));
        var index = new Dictionary<(int, ulong), List<int>>();
        for (int y = top; y < bottom; y++)
            for (int s = 0; s < k; s++)
            {
                int i = y * k + s;
                if (a.StripUniform[i]) continue;
                var key = (s, a.Strip[i]);
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>(1);
                if (list.Count < 8) list.Add(y); // cap very repetitive rows
            }

        var votes = new Dictionary<int, int>();
        int distinctive = 0, unchanged = 0;
        for (int y = top; y < bottom; y++)
            for (int s = 0; s < k; s++)
            {
                int i = y * k + s;
                if (b.StripUniform[i]) continue;
                distinctive++;
                if (b.Strip[i] == a.Strip[i]) unchanged++;
                if (!index.TryGetValue((s, b.Strip[i]), out var positions)) continue;
                foreach (int p in positions)
                {
                    int d = p - y;
                    if (d > 0) votes[d] = votes.GetValueOrDefault(d) + 1;
                }
            }

        if (distinctive == 0)
        {
            // Blank body (e.g. an empty stretch taller than the window): assume the usual scroll amount.
            return expected is int ex && ex > 0 && ex < body && Verify(ex) ? (ex, 0) : (null, 0);
        }
        if (unchanged >= distinctive * 0.98) return (0, 0);   // the window didn't scroll

        var candidates = votes
            .Where(kv => kv.Value >= 3 && body - kv.Key >= minUsefulOverlap)
            .Select(kv => (Dy: kv.Key, Votes: kv.Value, Score: kv.Value / (double)((body - kv.Key) * k)))
            .ToList();
        if (candidates.Count == 0)
        {
            // Nothing to vote with (e.g. the previous frame was blank): accept the usual scroll amount if it fits.
            return expected is int ex2 && ex2 > 0 && ex2 < body && Verify(ex2) ? (ex2, 0) : (null, 0);
        }

        // Pages often repeat some rows exactly (e.g. the descender slice of the same sentence in every
        // list item), which makes wrong offsets collect votes too. So verify the strongest candidates
        // over the whole overlap and keep the one that matches best; the expected offset only breaks
        // genuine ties (truly periodic content, where every candidate matches equally well).
        // Acceptance uses the row-majority ratio (robust to sticky boxes); ranking uses the share of
        // individual cells that match, which separates the true offset (~99%) from offsets where only
        // repeated parts line up (e.g. the same sentence in every list item, ~70%).
        var verified = candidates
            .OrderByDescending(c => c.Score)
            .Take(16)
            .Select(c => (c.Dy, Ratio: Measure(c.Dy)))
            .Where(c => c.Ratio.Rows >= minMatch)
            .ToList();
        if (expected is int ev && ev > 0 && ev < body && verified.All(c => c.Dy != ev))
        {
            var r = Measure(ev);
            if (r.Rows >= minMatch) verified.Add((ev, r));
        }
        if (verified.Count == 0) return (null, 0);

        double bestCells = verified.Max(c => c.Ratio.Cells);
        var tied = verified.Where(c => c.Ratio.Cells >= bestCells - 0.005).ToList();
        var pick = expected is int e
            ? tied.MinBy(c => Math.Abs(c.Dy - e))
            : tied.MinBy(c => c.Dy);
        return (pick.Dy, pick.Ratio.Skip);

        bool Verify(int dy) => Measure(dy).Rows >= minMatch;

        // Share of overlap rows that moved by dy. Per row, cells (strips) that stayed exactly in place
        // while not matching the shifted content are sticky elements (a sidebar box, share buttons) and
        // are ignored; the row counts as matched when most of its remaining cells match. A strip that
        // is half article, half sticky box can then no longer fail the whole row.
        // A band of failing rows right at the top (a see-through nav bar) is left out and reported as
        // Skip; that's safe because new content is always taken from the bottom of the frame.
        (double Rows, double Cells, int Skip) Measure(int dy)
        {
            int overlap = body - dy;
            if (overlap < minOverlap) return (0, 0, 0);
            var ok = new sbyte[overlap];          // 1 = moved with the content, 0 = didn't, -1 = stayed in place
            var cellsMatched = new int[overlap];
            var cellsCounted = new int[overlap];
            for (int y = top; y < bottom - dy; y++)
            {
                int bi = y * k, ai = (y + dy) * k;
                int matched = 0, counted = 0;
                for (int s = 0; s < k; s++)
                {
                    ulong bv = b.Strip[bi + s];
                    if (bv == a.Strip[ai + s]) { matched++; counted++; }
                    else if (bv != a.Strip[bi + s]) counted++;
                }
                int i = y - top;
                cellsMatched[i] = matched;
                cellsCounted[i] = counted;
                ok[i] = counted == 0 ? (sbyte)-1 : matched * 10 >= counted * 6 ? (sbyte)1 : (sbyte)0;
            }

            // Leading band of failing rows (at most 20% of the body, at least 8 rows, mostly failing).
            int skip = 0, lastBad = -1, bad = 0, seen = 0;
            for (int i = 0; i < Math.Min(overlap, body / 5); i++)
            {
                if (ok[i] < 0) continue;
                seen++;
                if (ok[i] == 0) { bad++; lastBad = i; }
            }
            if (lastBad >= 7)
            {
                int badInBand = 0, seenInBand = 0;
                for (int i = 0; i <= lastBad; i++) { if (ok[i] < 0) continue; seenInBand++; if (ok[i] == 0) badInBand++; }
                if (badInBand * 10 >= seenInBand * 6) skip = lastBad + 1;
            }

            int matchedRows = 0, countedRows = 0, matchedCells = 0, countedCells = 0;
            for (int i = skip; i < overlap; i++)
            {
                if (ok[i] < 0) continue; // the whole row stayed in place
                countedRows++;
                matchedCells += cellsMatched[i];
                countedCells += cellsCounted[i];
                if (ok[i] == 1) matchedRows++;
            }
            // Mostly static overlap tells us nothing.
            if (countedRows < (overlap - skip) / 2 || countedRows == 0) return (0, 0, 0);
            return (matchedRows / (double)countedRows, matchedCells / (double)countedCells, skip);
        }
    }

    /// <summary>
    /// For each column: did its pixels move by <paramref name="dy"/>, i.e. B[x, y] == A[x, y + dy] for
    /// almost all overlap rows? Static side panels, sticky boxes and the scrollbar (its thumb moves
    /// differently) fail; page margins pass. Rows that are completely static (a sticky nav bar that
    /// wasn't recognised as header) are skipped so they can't fail every column.
    /// </summary>
    public static bool[] ColumnsMovingBy(PixelBuffer a, PixelBuffer b, int dy, int top, int bottom, double minMatch = 0.99) =>
        ColumnsMovingBy(a, b, dy, top, bottom, minMatch, null, null);

    /// <param name="stickyOut">Optional (diagnostics): set to true for columns failed by the sticky test.</param>
    /// <param name="testableOut">Optional: set to true for columns this pair says something about, i.e. not
    /// one flat colour throughout the overlap (an empty margin or scrollbar track proves nothing).</param>
    public static bool[] ColumnsMovingBy(PixelBuffer a, PixelBuffer b, int dy, int top, int bottom, double minMatch,
        bool[]? stickyOut, bool[]? testableOut)
    {
        int w = a.Width;
        var ok = new bool[w];
        var matches = new int[w];
        var varies = new bool[w];
        uint[]? firstA = null, firstB = null;
        int rows = 0;
        // Leave out a few rows next to the header and footer: their shadows and glows (e.g. under a
        // nav button) are static and drawn over the scrolling page, which would fail those columns.
        int m = Math.Min(24, (bottom - top) / 10);
        if (bottom - dy - top - 2 * m < 16) m = 0;
        for (int y = top + m; y < bottom - dy - m; y++)
        {
            var rb = b.Row(y);
            if (rb.SequenceEqual(a.Row(y))) continue; // whole row stayed in place: sticky bar
            var ra = a.Row(y + dy);
            rows++;
            if (firstA == null) { firstA = ra.ToArray(); firstB = rb.ToArray(); }
            for (int x = 0; x < w; x++)
            {
                if (rb[x] == ra[x]) matches[x]++;
                if (rb[x] != firstB![x] || ra[x] != firstA[x]) varies[x] = true;
            }
        }
        if (rows == 0) return ok;
        for (int x = 0; x < w; x++) ok[x] = matches[x] >= rows * minMatch;
        if (testableOut != null)
            for (int x = 0; x < w; x++) testableOut[x] = varies[x] || !ok[x];

        // Sticky elements low in the window are often outside the overlap, so check them directly:
        // over the whole body, a column whose "detail" pixels (edges, text, borders - not flat
        // background) mostly stayed exactly in place while the page scrolled is a sticky element.
        var detail = new int[w];
        var inPlace = new int[w];
        for (int y = Math.Max(top + m, 1); y < Math.Min(bottom - m, a.Height - 1); y++)
        {
            var rb = b.Row(y);
            var ra = a.Row(y);
            if (rb.SequenceEqual(ra)) continue; // whole row static: handled as header/footer
            var up = b.Row(y - 1);
            var down = b.Row(y + 1);
            for (int x = 1; x < w - 1; x++)
            {
                uint p = rb[x];
                if (p == up[x] && p == down[x]) continue; // vertically flat: background, not detail
                detail[x]++;
                if (p == ra[x]) inPlace[x]++;
            }
        }
        for (int x = 0; x < w; x++)
            if (detail[x] >= 10 && inPlace[x] * 10 >= detail[x] * 9)
            {
                ok[x] = false; // truly sticky ≈ 100% in place
                if (stickyOut != null) stickyOut[x] = true;
                if (testableOut != null) testableOut[x] = true;
            }
        return ok;
    }

    /// <summary>The run of true columns containing the middle of the seed range (or the longest run).</summary>
    public static (int X0, int X1) RunAround(bool[] ok, int seedX0, int seedX1, int maxGap = 3) => PickRun(ok, seedX0, seedX1, maxGap);

    /// <summary>Single-pair version of <see cref="ColumnsMovingBy"/> + <see cref="RunAround"/>.</summary>
    public static (int X0, int X1) TranslationConsistentColumns(PixelBuffer a, PixelBuffer b, int dy, int top, int bottom,
        int seedX0, int seedX1, double minMatch = 0.99, int maxGap = 3) =>
        PickRun(ColumnsMovingBy(a, b, dy, top, bottom, minMatch), seedX0, seedX1, maxGap);

    private static (int X0, int X1) PickRun(bool[] ok, int seedX0, int seedX1, int maxGap)
    {
        int w = ok.Length;
        // The run containing the middle of the seed range.
        int mid = Math.Clamp((seedX0 + seedX1) / 2, 0, w - 1);
        if (!ok[mid])
        {
            // Middle column is noisy (e.g. an animation): fall back to the longest consistent run.
            return LongestRun(ok, maxGap) ?? (seedX0, seedX1);
        }
        int x0 = mid, x1 = mid + 1, gap = 0;
        for (int x = mid - 1; x >= 0; x--)
        {
            if (ok[x]) { x0 = x; gap = 0; } else if (++gap > maxGap) break;
        }
        gap = 0;
        for (int x = mid + 1; x < w; x++)
        {
            if (ok[x]) { x1 = x + 1; gap = 0; } else if (++gap > maxGap) break;
        }
        return (x0, x1);
    }

    /// <summary>Longest run of true values, allowing gaps of up to <paramref name="maxGap"/> false values inside it.</summary>
    public static (int X0, int X1)? LongestRun(bool[] flags, int maxGap)
    {
        (int, int)? best = null;
        int bestLen = 0, start = -1, lastTrue = -1;
        for (int x = 0; x <= flags.Length; x++)
        {
            bool on = x < flags.Length && flags[x];
            if (on)
            {
                if (start < 0 || x - lastTrue - 1 > maxGap)
                {
                    if (start >= 0 && lastTrue + 1 - start > bestLen) { bestLen = lastTrue + 1 - start; best = (start, lastTrue + 1); }
                    start = x;
                }
                lastTrue = x;
            }
        }
        if (start >= 0 && lastTrue + 1 - start > bestLen) best = (start, lastTrue + 1);
        return best;
    }
}
