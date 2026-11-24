using SnapLite.Core.Imaging;
using SnapLite.Core.Stitching;

namespace SnapLite.Tests;

/// <summary>
/// Simulates a window scrolling over a long page: optional sticky header, footer (status bar),
/// static left sidebar and a scrollbar whose thumb moves. The stitched result must equal the page.
/// </summary>
public class ScrollStitcherTests
{
    private sealed class FakeWindow
    {
        public int Width = 400, Height = 300, Header, Footer, Sidebar, Scrollbar;
        public PixelBuffer Page = null!;

        public int BodyHeight => Height - Header - Footer;
        public int ContentX0 => Sidebar;
        public int ContentX1 => Width - Scrollbar;
        public int MaxScroll => Page.Height - BodyHeight;

        public PixelBuffer Frame(int scroll)
        {
            var f = new PixelBuffer(Width, Height);
            for (int y = 0; y < Height; y++)
            {
                var row = f.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    uint c;
                    if (y < Header) c = Pattern(1_000_000 + y, x);                         // static header
                    else if (y >= Height - Footer) c = Pattern(2_000_000 + y, x);          // static footer
                    else if (x < Sidebar) c = Pattern(3_000_000 + y, x);                   // static sidebar
                    else if (x >= Width - Scrollbar) c = Thumb(scroll, y) ? 0xFF808080u : 0xFFEEEEEEu;
                    else c = Page[x - Sidebar, scroll + y - Header];                       // scrolling content
                    row[x] = c;
                }
            }
            return f;
        }

        private bool Thumb(int scroll, int y)
        {
            int track = BodyHeight;
            int thumbH = Math.Max(20, track * BodyHeight / Page.Height);
            int thumbTop = Header + (int)((long)(track - thumbH) * scroll / Math.Max(1, MaxScroll));
            return y >= thumbTop && y < thumbTop + thumbH;
        }

        /// <summary>The image a perfect stitch should produce: header + whole page + footer, content columns only.</summary>
        public PixelBuffer Expected(int lastScroll)
        {
            int pageRows = lastScroll + BodyHeight;
            var e = new PixelBuffer(ContentX1 - ContentX0, Header + pageRows + Footer);
            for (int y = 0; y < e.Height; y++)
                for (int x = 0; x < e.Width; x++)
                {
                    int wx = x + ContentX0;
                    e[x, y] = y < Header ? Pattern(1_000_000 + y, wx)
                        : y >= Header + pageRows ? Pattern(2_000_000 + (y - Header - pageRows) + Height - Footer, wx)
                        : Page[x, y - Header];
                }
            return e;
        }
    }

    /// <summary>Deterministic, distinctive colours per (row seed, x) — like text and pictures.</summary>
    private static uint Pattern(int seed, int x)
    {
        uint h = (uint)(seed * 2654435761u) ^ (uint)((x / 7) * 40503u);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return 0xFF000000u | (h & 0xFFFFFF);
    }

    private static PixelBuffer MakePage(int width, int height, Func<int, bool>? blankRow = null)
    {
        var p = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                p[x, y] = blankRow != null && blankRow(y) ? 0xFFFFFFFFu : Pattern(y, x);
        return p;
    }

    private static (ScrollStitcher Stitcher, int LastScroll, List<StitchStatus> Statuses) Run(FakeWindow w, IEnumerable<int> scrolls, int maxHeight = 100_000)
    {
        var s = new ScrollStitcher(maxHeight);
        var statuses = new List<StitchStatus>();
        int last = 0;
        foreach (int scroll in scrolls)
        {
            var r = s.Add(w.Frame(scroll));
            statuses.Add(r.Status);
            if (r.Status is StitchStatus.First or StitchStatus.Appended) last = scroll;
            if (r.Status is StitchStatus.NoMatch or StitchStatus.MaxHeightReached) break;
        }
        return (s, last, statuses);
    }

    private static IEnumerable<int> Steps(int max, int step)
    {
        for (int s = 0; s < max; s += step) yield return s;
        yield return max;
        yield return max; // scrolling at the very end changes nothing
    }

    private static void AssertSameImage(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"row {y} differs");
    }

    [Fact]
    public void Plain_scroll_reconstructs_the_page_exactly()
    {
        var w = new FakeWindow { Page = MakePage(400, 2000) };
        var (s, last, statuses) = Run(w, Steps(w.MaxScroll, 200));

        Assert.Equal(StitchStatus.NoChange, statuses[^1]);
        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Sticky_header_and_footer_appear_once()
    {
        var w = new FakeWindow { Header = 40, Footer = 20 };
        w.Page = MakePage(w.Width, 2500);
        var (s, last, _) = Run(w, Steps(w.MaxScroll, 150));

        Assert.Equal(40, s.HeaderHeight);
        Assert.Equal(20, s.FooterHeight);
        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Static_sidebar_and_scrollbar_are_left_out()
    {
        var w = new FakeWindow { Header = 30, Footer = 16, Sidebar = 80, Scrollbar = 14 };
        w.Page = MakePage(w.ContentX1 - w.ContentX0, 1800);
        var (s, last, _) = Run(w, Steps(w.MaxScroll, 170));

        Assert.Equal((80, 400 - 14), s.OutputColumns);
        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Variable_scroll_amounts_are_handled()
    {
        var w = new FakeWindow { Header = 25 };
        w.Page = MakePage(w.Width, 3000);
        var scrolls = new List<int> { 0, 37, 250, 251, 400, 610, 611, 800, 1100, 1350, 1600, 1900, 2200, 2500, w.MaxScroll, w.MaxScroll };
        var (s, last, _) = Run(w, scrolls);

        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Blank_stretches_between_content_still_align()
    {
        // Rows 600-900 and 1400-1500 are pure white (paragraph gaps, empty areas).
        var w = new FakeWindow();
        w.Page = MakePage(w.Width, 2200, y => y is >= 600 and < 900 or >= 1400 and < 1500);
        var (s, last, statuses) = Run(w, Steps(w.MaxScroll, 120));

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Window_that_does_not_scroll_returns_the_single_frame()
    {
        var w = new FakeWindow { Page = MakePage(400, 300) };
        var s = new ScrollStitcher();
        Assert.Equal(StitchStatus.First, s.Add(w.Frame(0)).Status);
        Assert.Equal(StitchStatus.NoChange, s.Add(w.Frame(0)).Status);
        Assert.Equal(300, s.Compose().Height);
    }

    [Fact]
    public void Max_height_truncates()
    {
        var w = new FakeWindow { Page = MakePage(400, 5000) };
        var (s, _, statuses) = Run(w, Steps(w.MaxScroll, 200), maxHeight: 1000);

        Assert.Equal(StitchStatus.MaxHeightReached, statuses[^1]);
        Assert.True(s.Truncated);
        Assert.Equal(1000, s.Compose().Height);
    }

    [Fact]
    public void Unrelated_frame_is_reported_as_no_match()
    {
        var w = new FakeWindow { Page = MakePage(400, 2000) };
        var other = new FakeWindow { Page = MakePage(400, 2000, y => false) };
        // A different page with the same generator but shifted seeds: nothing lines up.
        for (int y = 0; y < other.Page.Height; y++)
            for (int x = 0; x < other.Page.Width; x++)
                other.Page[x, y] = Pattern(y + 50_000, x);

        var s = new ScrollStitcher();
        s.Add(w.Frame(0));
        Assert.Equal(StitchStatus.NoMatch, s.Add(other.Frame(0)).Status);
    }

    [Fact]
    public void Scroll_larger_than_the_window_cannot_be_aligned()
    {
        var w = new FakeWindow { Page = MakePage(400, 3000) };
        var s = new ScrollStitcher();
        s.Add(w.Frame(0));
        Assert.Equal(StitchStatus.NoMatch, s.Add(w.Frame(w.BodyHeight + 50)).Status); // no overlap left
    }

    [Fact]
    public void Offset_finder_prefers_the_expected_offset_on_repetitive_content()
    {
        // Rows repeat every 70 px (a list of identical items) and the window scrolled 150 px:
        // offsets 10, 80, 150 and 220 all line up perfectly. Only the expected offset can decide.
        int h = 300;
        var ha = new ulong[h]; var hb = new ulong[h]; var u = new bool[h];
        for (int y = 0; y < h; y++) { ha[y] = (ulong)(y % 70) + 1; hb[y] = (ulong)((y + 150) % 70) + 1; }

        Assert.Equal(150, FrameAnalysis.FindOffset(ha, u, hb, u, 0, h, expected: 150));
        Assert.Equal(80, FrameAnalysis.FindOffset(ha, u, hb, u, 0, h, expected: 90));
    }

    [Fact]
    public void Partly_repeating_rows_do_not_fool_the_matcher_when_scroll_amount_changes()
    {
        // Reproduces a real Edge failure: list items 41 px tall where 12 rows of every item are identical
        // (the descender slice of the same sentence), the rest unique. The scroll step changes from
        // 200 to 500 px; offsets 500 - k·82 also collect votes and one of them is close to the old 200.
        var w = new FakeWindow { Width = 400, Height = 700, Header = 60, Footer = 6 };
        var page = new PixelBuffer(400, 4000);
        for (int y = 0; y < page.Height; y++)
        {
            int inItem = y % 41;
            int item = y / 41;
            for (int x = 0; x < page.Width; x++)
                page[x, y] = inItem is >= 25 and < 37
                    ? Pattern(900_000 + inItem + (item % 2) * 100, x)   // repeats every 2 items (82 px)
                    : Pattern(y, x);                                     // unique
        }
        w.Page = page;
        var (s, last, statuses) = Run(w, [0, 200, 700, 1200, 1700, 1900, 2400, 2900, w.MaxScroll, w.MaxScroll]);

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        AssertSameImage(w.Expected(last), s.Compose());
    }

    [Fact]
    public void Floating_element_that_does_not_scroll_is_tolerated()
    {
        // Reproduces a real Edge failure: a fixed 56×56 "chat bubble" near the bottom right of the
        // viewport stays put while the page scrolls, spoiling ~14% of the rows at the true offset.
        var w = new FakeWindow { Width = 600, Height = 700, Header = 50 };
        w.Page = MakePage(w.Width, 4000);
        var s = new ScrollStitcher();
        var statuses = new List<StitchStatus>();
        int last = 0;
        foreach (int scroll in new[] { 0, 200, 600, 1000, 1400, 1800, 2200, 2600, 3000, w.MaxScroll, w.MaxScroll })
        {
            var f = w.Frame(scroll);
            for (int y = 560; y < 616; y++) for (int x = 520; x < 576; x++) f[x, y] = 0xFFEE3333u; // the bubble
            var r = s.Add(f);
            statuses.Add(r.Status);
            if (r.Status is StitchStatus.First or StitchStatus.Appended) last = scroll;
        }

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        Assert.Equal(StitchStatus.NoChange, statuses[^1]);
        Assert.Equal(50 + last + w.BodyHeight, s.Compose().Height);
    }

    [Fact]
    public void Blog_layout_with_restyled_nav_sticky_sidebar_and_share_bar()
    {
        // Reproduces a real Chrome failure (a blog article): the result was a 60 px wide sliver.
        //  rows 0-40   browser toolbar (static)
        //  rows 40-80  site nav: different style at the very top than once scrolled
        //  cols 20-60  sticky share buttons (rows 300-420 of the window)
        //  cols 470-570 sticky "free guide" box (rows 150-450 of the window)
        //  cols 100-450 the article; everything else is white page background; scrollbar at the right
        const int W = 600, H = 700, Top = 80, Body = H - Top, PageH = 5000;
        PixelBuffer Frame(int scroll)
        {
            var f = new PixelBuffer(W, H);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    uint c;
                    if (y < 40) c = Pattern(1_000_000 + y, x);
                    else if (y < Top) c = Pattern((scroll == 0 ? 3_000_000 : 4_000_000) + y, x);
                    else if (x is >= 20 and < 60 && y is >= 300 and < 420) c = Pattern(5_000_000 + y, x);
                    else if (x is >= 470 and < 570 && y is >= 150 and < 450) c = Pattern(6_000_000 + y, x);
                    else if (x >= W - 12) c = (y - Top) * PageH / Body / 8 == scroll * 1 / 40 ? 0xFF808080u : 0xFFEEEEEEu;
                    else if (x is >= 100 and < 450) c = Pattern(scroll + y - Top, x);
                    else c = 0xFFFFFFFFu;
                    f[x, y] = c;
                }
            return f;
        }

        var s = new ScrollStitcher();
        var statuses = new List<StitchStatus>();
        int last = 0;
        foreach (int scroll in new[] { 0, 200, 600, 1000, 1400, 1800, 2200, 2600, 3000, 3400, 3800, 4200, PageH - Body, PageH - Body })
        {
            var r = s.Add(Frame(scroll));
            statuses.Add(r.Status);
            if (r.Status is StitchStatus.First or StitchStatus.Appended) last = scroll;
        }

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        Assert.Equal(StitchStatus.NoChange, statuses[^1]);
        Assert.Equal((60, 470), s.OutputColumns);   // article + margins, without share bar and sticky box

        var img = s.Compose();
        Assert.Equal(Top + last + Body, img.Height);
        for (int y = Top; y < img.Height; y++)
            for (int x = 100; x < 450; x++)
                Assert.True(img[x - 60, y] == Pattern(y - Top, x), $"article pixel ({x},{y}) is wrong");
    }

    [Fact]
    public void Glow_under_a_nav_button_does_not_crop_the_article()
    {
        // Reproduces a real Chrome result: the article was cut off exactly below the nav's "Bundle"
        // button, whose glow is static and drawn over the scrolling page just under the nav.
        const int W = 600, H = 700, Top = 80, Body = H - Top, PageH = 5000;
        PixelBuffer Frame(int scroll)
        {
            var f = new PixelBuffer(W, H);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    uint c;
                    if (y < Top) c = Pattern(1_000_000 + y, x);
                    else if (x is >= 470 and < 570 && y is >= 150 and < 450) c = Pattern(6_000_000 + y, x);
                    else if (x is >= 100 and < 450) c = Pattern(scroll + y - Top, x);
                    else c = 0xFFFFFFFFu;
                    if (y is >= Top and < Top + 10 && x is >= 300 and < 380)
                        c = 0xFF000000u | (((c & 0xFEFEFEu) >> 1) + (uint)(0x30 - (y - Top) * 4) * 0x010001u); // purple glow, fading
                    f[x, y] = c;
                }
            return f;
        }

        var s = new ScrollStitcher();
        var statuses = new List<StitchStatus>();
        foreach (int scroll in new[] { 0, 200, 700, 1200, 1700, 2200, 2700, 3200, 3700, 4200, PageH - Body, PageH - Body })
            statuses.Add(s.Add(Frame(scroll)).Status);

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        Assert.Equal((0, 470), s.OutputColumns); // whole article; only the sticky box is left out
    }

    [Fact]
    public void See_through_nav_bar_is_treated_as_header()
    {
        // Reproduces a real Chrome failure: the site's sticky nav (rows 40-105) is semi-transparent, so
        // the page shows through it and it is never identical between screens, nor does it move.
        // With a big scroll step its rows were ~18% of the overlap and the match failed.
        const int W = 600, H = 900, Nav0 = 40, Nav1 = 105;
        var w = new FakeWindow { Width = W, Height = H, Header = Nav0 };
        w.Page = MakePage(W, 6000);
        PixelBuffer Frame(int scroll)
        {
            var f = w.Frame(scroll);
            for (int y = Nav0; y < Nav1; y++)
                for (int x = 0; x < W; x++)
                    f[x, y] = (Pattern(7_000_000 + y, x) & 0xFFF0F0F0u) | (f[x, y] & 0x000F0F0Fu); // nav blended with the page behind
            return f;
        }

        var s = new ScrollStitcher();
        var statuses = new List<StitchStatus>();
        int last = 0;
        var scrolls = new List<int> { 0, 200 };
        for (int sc = 800; sc < w.MaxScroll; sc += 600) scrolls.Add(sc); // big steps: ~70% of the window
        scrolls.Add(w.MaxScroll); scrolls.Add(w.MaxScroll);
        foreach (int scroll in scrolls)
        {
            var r = s.Add(Frame(scroll));
            statuses.Add(r.Status);
            if (r.Status is StitchStatus.First or StitchStatus.Appended) last = scroll;
        }

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        Assert.Equal(StitchStatus.NoChange, statuses[^1]);
        Assert.Equal(Nav1, s.HeaderHeight);
        var img = s.Compose();
        Assert.Equal(Nav0 + last + w.BodyHeight, img.Height);
        // Everything below the nav must be the page, row for row.
        for (int y = Nav1; y < img.Height; y++)
            Assert.True(img.Row(y).SequenceEqual(w.Page.Row(y - Nav0)), $"page row {y - Nav0} is wrong");
    }

    [Fact]
    public void Blinking_cursor_does_not_stop_end_of_page_detection()
    {
        // A text cursor in the page toggles on every screenshot, so no two frames are pixel-identical.
        var w = new FakeWindow { Header = 30 };
        w.Page = MakePage(w.Width, 2000);
        var s = new ScrollStitcher();
        var statuses = new List<StitchStatus>();
        int n = 0, last = 0;
        foreach (int scroll in Steps(w.MaxScroll, 150).Append(w.MaxScroll))
        {
            var f = w.Frame(scroll);
            if (n++ % 2 == 0) for (int y = 200; y < 218; y++) f[120, y] = 0xFF000000u; // the cursor, on/off
            var r = s.Add(f);
            statuses.Add(r.Status);
            if (r.Status is StitchStatus.First or StitchStatus.Appended) last = scroll;
        }

        Assert.DoesNotContain(StitchStatus.NoMatch, statuses);
        Assert.Equal(StitchStatus.NoChange, statuses[^1]);
        Assert.Equal(StitchStatus.NoChange, statuses[^2]);
        Assert.Equal(30 + last + w.BodyHeight, s.Compose().Height);
    }

    [Fact]
    public void Changed_fraction_ignores_a_blinking_cursor_but_not_a_scroll()
    {
        var w = new FakeWindow { Page = MakePage(400, 2000) };
        var a = w.Frame(0);
        var blink = w.Frame(0);
        for (int y = 100; y < 118; y++) blink[50, y] = 0xFF000000u;

        Assert.Equal(0, FrameAnalysis.ChangedFraction(a, w.Frame(0)));
        Assert.True(FrameAnalysis.ChangedFraction(a, blink) < 0.01);
        Assert.True(FrameAnalysis.ChangedFraction(a, w.Frame(120)) > 0.5);
    }

    [Fact]
    public void Longest_run_allows_small_gaps()
    {
        var flags = new bool[40];
        for (int i = 5; i < 15; i++) flags[i] = true;
        flags[16] = flags[17] = true;           // gap of 1 at 15 → same run
        for (int i = 25; i < 28; i++) flags[i] = true;
        Assert.Equal((5, 18), FrameAnalysis.LongestRun(flags, maxGap: 2));
    }
}
