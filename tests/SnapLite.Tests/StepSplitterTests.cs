using SnapLite.Core.Splitting;

namespace SnapLite.Tests;

public class StepSplitterTests
{
    private const int Min = 400, Max = 1600;

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Text lines of <paramref name="lineHeight"/> px that touch each other, from top to bottom.</summary>
    private static IEnumerable<Span> Lines(int top, int bottom, int lineHeight = 24)
    {
        for (int y = top; y + lineHeight <= bottom; y += lineHeight) yield return new Span(y, y + lineHeight);
    }

    /// <summary>Paragraphs of <paramref name="linesPer"/> lines separated by <paramref name="gap"/> px of whitespace.</summary>
    private static List<Span> Paragraphs(int top, int bottom, int linesPer = 4, int gap = 16, int lineHeight = 24)
    {
        var zones = new List<Span>();
        int y = top;
        while (y + linesPer * lineHeight <= bottom)
        {
            zones.AddRange(Lines(y, y + linesPer * lineHeight, lineHeight));
            y += linesPer * lineHeight + gap;
        }
        return zones;
    }

    private static void AssertValid(IReadOnlyList<Step> steps, int pageHeight, int min = Min, int max = Max)
    {
        Assert.NotEmpty(steps);
        Assert.Equal(0, steps[0].Top);
        Assert.Equal(pageHeight, steps[^1].Bottom);
        for (int i = 0; i < steps.Count; i++)
        {
            Assert.Equal(i + 1, steps[i].Index);
            Assert.True(steps[i].Height <= max, $"step {i + 1} is {steps[i].Height} px > max {max}");
            if (i < steps.Count - 1)
            {
                Assert.Equal(steps[i].Bottom, steps[i + 1].Top); // contiguous, no gaps or overlap
                Assert.True(steps[i].Height >= min, $"step {i + 1} is {steps[i].Height} px < min {min}");
            }
        }
    }

    private static void AssertNoCutInside(IReadOnlyList<Step> steps, IEnumerable<Span> zones)
    {
        foreach (var s in steps.Take(steps.Count - 1))
            foreach (var z in zones)
                Assert.False(z.StrictlyContains(s.Bottom), $"cut at {s.Bottom} goes through content {z.Top}-{z.Bottom}");
    }

    // ---- smart split -------------------------------------------------------------

    [Fact]
    public void Short_page_is_a_single_step()
    {
        var steps = StepSplitter.SmartSplit(PageLayout.Empty(300), Min, Max);
        Assert.Single(steps);
        Assert.Equal((0, 300), (steps[0].Top, steps[0].Bottom));
    }

    [Fact]
    public void Each_section_becomes_a_step_when_sizes_allow()
    {
        // 6 sections of 600 px each.
        var boundaries = Enumerable.Range(1, 5).Select(i => new Boundary(i * 600, BoundaryKind.Section)).ToList();
        var zones = Enumerable.Range(0, 6).SelectMany(i => Paragraphs(i * 600 + 40, i * 600 + 560)).ToList();
        var layout = new PageLayout(3600, boundaries, zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 3600);
        Assert.Equal(new[] { 600, 1200, 1800, 2400, 3000, 3600 }, steps.Select(s => s.Bottom));
    }

    [Fact]
    public void Heading_that_starts_a_pixel_inside_its_own_text_line_still_starts_the_step()
    {
        // Reproduces a GitHub wiki page: each h2 element starts 1 px below the top of its text line,
        // so the heading looked like "a cut through text" and steps began ~80 px above the heading.
        var zones = new List<Span>();
        var boundaries = new List<Boundary>();
        var headings = new List<Heading>();
        int y = 20;
        for (int section = 0; section < 4; section++)
        {
            if (section > 0)
            {
                zones.Add(new Span(y, y + 32));                       // heading text line
                boundaries.Add(new Boundary(y + 1, BoundaryKind.Heading2)); // element box 1 px lower
                headings.Add(new Heading(y + 1, 2, $"Section {section}"));
                y += 32 + 24;
            }
            zones.AddRange(Paragraphs(y, y + 700));                     // body, with gaps between paragraphs
            y += 700 + 40;
        }
        var layout = new PageLayout(y, boundaries, zones, headings);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, y);
        AssertNoCutInside(steps, zones);
        Assert.Equal(4, steps.Count);
        for (int i = 1; i < steps.Count; i++)
        {
            int headingTextTop = boundaries[i - 1].Y - 1;
            Assert.InRange(headingTextTop - steps[i].Top, 0, 16); // starts just above the heading, with a little room
            Assert.Equal($"Section {i}", steps[i].Heading);
        }
    }

    [Fact]
    public void A_footer_on_its_own_joins_the_last_step()
    {
        // Three 600 px sections, then a 160 px footer (its own section). The footer alone would be a
        // nearly empty last image, so it belongs with the section above it.
        var boundaries = new List<Boundary> { new(600, BoundaryKind.Section), new(1200, BoundaryKind.Section), new(1800, BoundaryKind.Section) };
        var zones = Enumerable.Range(0, 3).SelectMany(i => Paragraphs(i * 600 + 40, i * 600 + 560)).Concat([new Span(1830, 1930)]).ToList();
        var layout = new PageLayout(1960, boundaries, zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 1960);
        Assert.Equal(new[] { 600, 1200, 1960 }, steps.Select(s => s.Bottom));
    }

    [Fact]
    public void Small_sections_are_merged_to_respect_min_height()
    {
        // 15 sections of 200 px: each step must hold at least two of them.
        var boundaries = Enumerable.Range(1, 14).Select(i => new Boundary(i * 200, BoundaryKind.Section)).ToList();
        var layout = new PageLayout(3000, boundaries, [], []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 3000);
        Assert.All(steps.Take(steps.Count - 1), s => Assert.Equal(0, s.Bottom % 200)); // still cut on section edges
    }

    [Fact]
    public void Tall_section_is_split_in_whitespace_never_through_text()
    {
        // One 5000 px section of paragraphs, no structural boundaries inside.
        var zones = Paragraphs(20, 4980);
        var layout = new PageLayout(5000, [], zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 5000);
        Assert.True(steps.Count >= 4);
        AssertNoCutInside(steps, zones);
    }

    [Fact]
    public void Paragraph_taller_than_max_is_cut_between_lines_not_through_a_line()
    {
        var zones = Lines(0, 4000).ToList(); // one giant paragraph, lines touch, no whitespace at all
        var layout = new PageLayout(4000, [], zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 4000);
        AssertNoCutInside(steps, zones);
        Assert.All(steps.Take(steps.Count - 1), s => Assert.Equal(0, s.Bottom % 24));
    }

    [Fact]
    public void Image_taller_than_max_still_gives_valid_steps()
    {
        // Unavoidable: a 3000 px image has to be cut somewhere, but every step stays within limits.
        var zones = new List<Span> { new(100, 3100) };
        var layout = new PageLayout(3300, [], zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 3300);
    }

    [Fact]
    public void Prefers_h2_over_paragraph_gaps()
    {
        // 2400 px of paragraphs with an h2 at 1000: the first cut should land on the h2.
        var zones = Paragraphs(0, 2400).Where(z => z.Bottom <= 990 || z.Top >= 1010).ToList();
        var layout = new PageLayout(2400, [new Boundary(1000, BoundaryKind.Heading2)], zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 2400);
        Assert.Contains(steps, s => s.Bottom == 1000);
    }

    [Fact]
    public void Weak_h3_is_not_used_when_page_fits_without_it()
    {
        var zones = Paragraphs(0, 1200).Where(z => z.Bottom <= 590 || z.Top >= 610).ToList();
        var layout = new PageLayout(1200, [new Boundary(600, BoundaryKind.Heading3)], zones, []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        Assert.Single(steps); // 1200 px fits in one step; an h3 alone isn't worth a cut
    }

    [Fact]
    public void Short_last_step_is_allowed_only_at_the_end()
    {
        var boundaries = new[] { new Boundary(1500, BoundaryKind.Section) };
        var layout = new PageLayout(1650, boundaries, [], []);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        AssertValid(steps, 1650);
    }

    [Fact]
    public void Steps_are_named_after_their_headings()
    {
        var boundaries = new[] { new Boundary(800, BoundaryKind.Section), new Boundary(1600, BoundaryKind.Section) };
        var headings = new[]
        {
            new Heading(20, 1, "Welcome"),
            new Heading(820, 2, "Features"),
            new Heading(900, 3, "Fast"),
            new Heading(1620, 2, "Pricing"),
        };
        var layout = new PageLayout(2400, boundaries, [], headings);

        var steps = StepSplitter.SmartSplit(layout, Min, Max);

        Assert.Equal(new[] { "Welcome", "Features", "Pricing" }, steps.Select(s => s.Heading));
    }

    [Fact]
    public void Step_without_heading_continues_the_previous_one()
    {
        var steps = StepSplitter.SimpleSplit(2000, 1000, 0, [new Heading(10, 2, "Intro")]);
        Assert.Equal("Intro", steps[0].Heading);
        Assert.Equal("Intro (continued)", steps[1].Heading);
    }

    [Theory]
    [InlineData(400, 1600)]
    [InlineData(300, 800)]
    [InlineData(1000, 1200)]
    public void Random_layouts_always_produce_valid_steps(int min, int max)
    {
        var rng = new Random(1234 + min);
        for (int round = 0; round < 50; round++)
        {
            int height = rng.Next(500, 20000);
            var boundaries = Enumerable.Range(0, rng.Next(0, 40))
                .Select(_ => new Boundary(rng.Next(1, height), (BoundaryKind)rng.Next(0, 4))).ToList();
            var zones = Enumerable.Range(0, rng.Next(0, 400))
                .Select(_ => { int t = rng.Next(0, height); return new Span(t, t + rng.Next(5, 300)); }).ToList();

            var steps = StepSplitter.SmartSplit(new PageLayout(height, boundaries, zones, []), min, max);

            AssertValid(steps, height, Math.Min(min, max - 100), max);
        }
    }

    // ---- simple split ----------------------------------------------------------

    [Fact]
    public void Simple_split_covers_the_page_with_overlap()
    {
        var steps = StepSplitter.SimpleSplit(2500, 1000, 50);

        Assert.Equal(new[] { (0, 1000), (950, 1950), (1900, 2500) }, steps.Select(s => (s.Top, s.Bottom)));
    }

    [Fact]
    public void Simple_split_without_overlap_tiles_exactly()
    {
        var steps = StepSplitter.SimpleSplit(3000, 1000, 0);
        Assert.Equal(new[] { 1000, 2000, 3000 }, steps.Select(s => s.Bottom));
    }

    [Fact]
    public void Simple_split_clamps_silly_overlap()
    {
        var steps = StepSplitter.SimpleSplit(3000, 1000, 5000); // overlap capped at half a chunk
        AssertValidOverlapping(steps, 3000);
        Assert.Equal(500, steps[0].Bottom - steps[1].Top);
    }

    private static void AssertValidOverlapping(IReadOnlyList<Step> steps, int height)
    {
        Assert.Equal(0, steps[0].Top);
        Assert.Equal(height, steps[^1].Bottom);
        for (int i = 1; i < steps.Count; i++) Assert.True(steps[i].Top < steps[i - 1].Bottom);
    }

    // ---- zones -----------------------------------------------------------------

    [Fact]
    public void Overlapping_zones_merge_but_touching_lines_stay_separate()
    {
        var merged = StepSplitter.MergeZones([new(0, 24), new(24, 48), new(40, 60), new(100, 120), new(110, 115)], 1000);
        Assert.Equal(new[] { new Span(0, 24), new Span(24, 60), new Span(100, 120) }, merged);
    }

    [Fact]
    public void IsInside_is_strict()
    {
        var merged = StepSplitter.MergeZones([new(100, 200)], 1000);
        Assert.False(StepSplitter.IsInside(merged, 100));
        Assert.True(StepSplitter.IsInside(merged, 150));
        Assert.False(StepSplitter.IsInside(merged, 200));
    }
}
