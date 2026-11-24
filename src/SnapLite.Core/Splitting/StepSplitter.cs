namespace SnapLite.Core.Splitting;

/// <summary>
/// Splits a tall page into "steps".
///
/// <para><b>Smart split</b> picks cut positions with dynamic programming. Every candidate cut has a
/// weight (how natural a place it is to start a new step):</para>
/// <list type="bullet">
/// <item>section edge 10, h1 9, h2 7, h3 4,</item>
/// <item>whitespace gap between content: 1–6 depending on its size,</item>
/// <item>between two touching text lines: 0.</item>
/// </list>
/// <para>The cost of a set of cuts is Σ(10 − 2·weight) per cut, so cuts with weight &gt; 5 lower the
/// cost (every section/h1/h2 becomes its own step when the size limits allow it) and weaker ones are
/// only used when a section is taller than the maximum. Cutting through a text line or image costs
/// 1000 and only happens when nothing else fits. Each step must be Min..Max tall; only the last step
/// may be shorter, with a penalty. This merges sections that are too small and splits ones that are
/// too tall in one pass.</para>
/// </summary>
public static class StepSplitter
{
    public const int SectionWeight = 10, H1Weight = 9, H2Weight = 7, H3Weight = 4;
    private const double InsideContentCost = 1000;
    private const int GridStep = 50;   // fallback candidates so a solution always exists
    private const int DedupeDistance = 6;

    public static int WeightOf(BoundaryKind kind) => kind switch
    {
        BoundaryKind.Section => SectionWeight,
        BoundaryKind.Heading1 => H1Weight,
        BoundaryKind.Heading2 => H2Weight,
        BoundaryKind.Heading3 => H3Weight,
        _ => 0,
    };

    /// <summary>Weight of a whitespace gap of <paramref name="gap"/> px between content.</summary>
    public static int GapWeight(int gap) => gap switch
    {
        >= 120 => 6,
        >= 48 => 4,
        >= 16 => 2,
        >= 1 => 1,
        _ => 0,
    };

    // ---- Simple split ------------------------------------------------------------

    /// <summary>Fixed-height chunks; each chunk repeats the last <paramref name="overlap"/> px of the previous one.</summary>
    public static IReadOnlyList<Step> SimpleSplit(int pageHeight, int chunkHeight, int overlap, IReadOnlyList<Heading>? headings = null)
    {
        if (pageHeight <= 0) return [];
        chunkHeight = Math.Max(50, chunkHeight);
        overlap = Math.Clamp(overlap, 0, chunkHeight / 2);

        var ranges = new List<(int Top, int Bottom)>();
        int top = 0;
        while (true)
        {
            int bottom = Math.Min(top + chunkHeight, pageHeight);
            ranges.Add((top, bottom));
            if (bottom >= pageHeight) break;
            top = bottom - overlap;
        }
        return Label(ranges, headings ?? []);
    }

    // ---- Smart split ---------------------------------------------------------------

    public static IReadOnlyList<Step> SmartSplit(PageLayout layout, int minHeight, int maxHeight)
    {
        int h = layout.Height;
        if (h <= 0) return [];
        maxHeight = Math.Max(200, maxHeight);
        minHeight = Math.Clamp(minHeight, 50, maxHeight - 2 * GridStep); // keep a window wide enough for grid candidates

        if (h <= minHeight) return Label([(0, h)], layout.Headings);

        var zones = MergeZones(layout.ContentZones, h);
        var nodes = BuildCandidates(layout, zones);

        // dp[i] = cheapest cost to end a step exactly at nodes[i]
        int n = nodes.Count;
        var dp = new double[n];
        var prev = new int[n];
        Array.Fill(dp, double.PositiveInfinity);
        Array.Fill(prev, -1);
        dp[0] = 0;
        double ideal = (minHeight + maxHeight) / 2.0;

        int windowStart = 0;
        for (int j = 1; j < n; j++)
        {
            int yj = nodes[j].Y;
            bool last = j == n - 1;
            while (yj - nodes[windowStart].Y > maxHeight) windowStart++;

            for (int i = windowStart; i < j; i++)
            {
                if (double.IsPositiveInfinity(dp[i])) continue;
                int len = yj - nodes[i].Y;
                double cost;
                if (len >= minHeight) cost = 0;
                // A short last step is allowed but not loved: a footer on its own (say a third of the minimum)
                // costs more than a section edge saves, so it joins the previous step when that fits.
                else if (last) cost = 25.0 * (minHeight - len) / minHeight;
                else continue;

                cost += last ? 0 : nodes[j].Cost;
                cost += 0.01 * Math.Abs(len - ideal) / ideal; // tie-break: prefer balanced steps
                double total = dp[i] + cost;
                if (total < dp[j])
                {
                    dp[j] = total;
                    prev[j] = i;
                }
            }
        }

        var ranges = new List<(int, int)>();
        if (double.IsPositiveInfinity(dp[n - 1]))
        {
            // Can't happen with the grid candidates, but never fail: fall back to fixed chunks.
            return SimpleSplit(h, maxHeight, 0, layout.Headings);
        }
        for (int j = n - 1; j > 0; j = prev[j]) ranges.Add((nodes[prev[j]].Y, nodes[j].Y));
        ranges.Reverse();
        return Label(ranges, layout.Headings);
    }

    private readonly record struct Node(int Y, double Cost);

    /// <summary>Candidate cut positions (including 0 and the page bottom), sorted and de-duplicated.</summary>
    private static List<Node> BuildCandidates(PageLayout layout, IReadOnlyList<Span> zones)
    {
        int h = layout.Height;
        var raw = new List<(int Y, int Weight)>();

        foreach (var b in layout.Boundaries)
        {
            int y = SnapToWhitespace(zones, b.Y);
            if (y > 0 && y < h) raw.Add((y, WeightOf(b.Kind)));
        }

        // Whitespace between content zones (and touching zones = between two text lines).
        int prevBottom = 0;
        foreach (var z in zones)
        {
            int gap = z.Top - prevBottom;
            if (prevBottom > 0 && gap >= 0) raw.Add((prevBottom + gap / 2, GapWeight(gap)));
            else if (prevBottom == 0 && gap > 0) raw.Add((gap / 2, GapWeight(gap)));
            prevBottom = Math.Max(prevBottom, z.Bottom);
        }

        // Grid fallback so every window has at least one candidate.
        for (int y = GridStep; y < h; y += GridStep) raw.Add((y, -1));

        // Turn into costed nodes. Weight -1 marks grid points (cost 10 if in whitespace).
        var nodes = new List<Node>(raw.Count + 2);
        foreach (var (y, w) in raw)
        {
            bool inside = IsInside(zones, y);
            double cost = inside ? InsideContentCost : 10 - 2 * Math.Max(0, w);
            nodes.Add(new Node(y, cost));
        }
        nodes.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Cost.CompareTo(b.Cost));

        // Keep the cheapest node within each small neighbourhood.
        var result = new List<Node> { new(0, 0) };
        foreach (var node in nodes)
        {
            var last = result[^1];
            if (node.Y - last.Y < DedupeDistance && result.Count > 1)
            {
                if (node.Cost < last.Cost) result[^1] = node;
            }
            else if (node.Y - last.Y >= DedupeDistance)
            {
                result.Add(node);
            }
        }
        if (h - result[^1].Y < DedupeDistance && result.Count > 1) result.RemoveAt(result.Count - 1);
        result.Add(new Node(h, 0));
        return result;
    }

    /// <summary>
    /// A heading's or section's edge often sits a pixel or two inside its own first text line (the
    /// element box and the text box don't line up exactly; on GitHub an h2 starts 1 px below its text).
    /// Taken literally that's "a cut through text" and the heading loses out to some gap nearby, so
    /// edges within <see cref="SnapDistance"/> of a zone's top or bottom move into the whitespace next
    /// to it, leaving a little room above the heading.
    /// </summary>
    public static int SnapToWhitespace(IReadOnlyList<Span> merged, int y)
    {
        int i = IndexOfZoneContaining(merged, y);
        if (i < 0) return y;
        var z = merged[i];
        if (y - z.Top <= SnapDistance)
        {
            int above = i > 0 ? merged[i - 1].Bottom : 0;
            return z.Top - Math.Min(HeadingMargin, Math.Max(0, z.Top - above) / 2);
        }
        if (z.Bottom - y <= SnapDistance)
        {
            int below = i + 1 < merged.Count ? merged[i + 1].Top : z.Bottom;
            return z.Bottom + Math.Min(HeadingMargin, Math.Max(0, below - z.Bottom) / 2);
        }
        return y; // really in the middle of something
    }

    private const int SnapDistance = 16, HeadingMargin = 16;

    private static int IndexOfZoneContaining(IReadOnlyList<Span> merged, int y)
    {
        int lo = 0, hi = merged.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var z = merged[mid];
            if (y <= z.Top) hi = mid - 1;
            else if (y >= z.Bottom) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>Sorts zones and merges ones that overlap. Touching zones (consecutive text lines) stay separate.</summary>
    public static IReadOnlyList<Span> MergeZones(IReadOnlyList<Span> zones, int pageHeight)
    {
        var sorted = zones
            .Select(z => new Span(Math.Max(0, z.Top), Math.Min(pageHeight, z.Bottom)))
            .Where(z => z.Bottom > z.Top)
            .OrderBy(z => z.Top)
            .ToList();

        var merged = new List<Span>(sorted.Count);
        foreach (var z in sorted)
        {
            if (merged.Count > 0 && z.Top < merged[^1].Bottom)
                merged[^1] = new Span(merged[^1].Top, Math.Max(merged[^1].Bottom, z.Bottom));
            else
                merged.Add(z);
        }
        return merged;
    }

    /// <summary>True if y falls strictly inside a merged zone (binary search).</summary>
    public static bool IsInside(IReadOnlyList<Span> merged, int y)
    {
        int lo = 0, hi = merged.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var z = merged[mid];
            if (y <= z.Top) hi = mid - 1;
            else if (y >= z.Bottom) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// Names each step after the first heading inside it, or "… (continued)" after the last heading above it.
    /// </summary>
    private static IReadOnlyList<Step> Label(IReadOnlyList<(int Top, int Bottom)> ranges, IReadOnlyList<Heading> headings)
    {
        var sorted = headings.Where(x => !string.IsNullOrWhiteSpace(x.Text)).OrderBy(x => x.Y).ToList();
        var steps = new List<Step>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            var (top, bottom) = ranges[i];
            string? label = sorted
                .Where(x => x.Y >= top && x.Y < bottom)
                .OrderBy(x => x.Level).ThenBy(x => x.Y) // the most important heading in the step
                .Select(x => x.Text)
                .FirstOrDefault();
            if (label == null)
            {
                var above = sorted.LastOrDefault(x => x.Y < top);
                if (above.Text != null) label = above.Text + " (continued)";
            }
            steps.Add(new Step(i + 1, top, bottom, label));
        }
        return steps;
    }
}
