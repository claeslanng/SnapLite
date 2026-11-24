using SnapLite.Core.Splitting;

namespace SnapLite.Tests;

public class StepsIndexWriterTests
{
    [Fact]
    public void Lists_every_step_with_heading_and_image()
    {
        var steps = new[] { new Step(1, 0, 800, "Welcome"), new Step(2, 800, 1500, null) };
        string md = StepsIndexWriter.Build("My Page", "https://example.com/", new DateTime(2026, 11, 23, 14, 5, 0),
            "smart", steps, ["My Page_01.png", "My Page_02.png"], "My Page_full.png");

        Assert.Contains("# My Page", md);
        Assert.Contains("Source: <https://example.com/>", md);
        Assert.Contains("2026-11-23 14:05 · 2 steps (smart)", md);
        Assert.Contains("## 1. Welcome", md);
        Assert.Contains("![Step 1](My%20Page_01.png)", md);
        Assert.Contains("## 2. Step 2", md); // no heading → generic name
        Assert.Contains("[My Page\\_full.png](My%20Page_full.png)", md);
    }

    [Fact]
    public void Escapes_markdown_in_headings()
    {
        Assert.Equal(@"\# 1 \*best\* \[deal\]", StepsIndexWriter.Escape("# 1 *best* [deal]"));
    }

    [Fact]
    public void Requires_one_file_per_step()
    {
        Assert.Throws<ArgumentException>(() =>
            StepsIndexWriter.Build("t", null, DateTime.Now, "simple", [new Step(1, 0, 10, null)], []));
    }
}
