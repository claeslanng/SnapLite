namespace SnapLite.Core.Splitting;

/// <summary>What kind of structural boundary a candidate cut position comes from.</summary>
public enum BoundaryKind
{
    /// <summary>Top or bottom edge of a header/section/article/main child etc.</summary>
    Section,
    Heading1,
    Heading2,
    Heading3,
}

/// <summary>A place where a new step could naturally start (page y in pixels).</summary>
public readonly record struct Boundary(int Y, BoundaryKind Kind);

/// <summary>A vertical range [Top, Bottom) that a cut should not go through (a text line, an image…).</summary>
public readonly record struct Span(int Top, int Bottom)
{
    public int Height => Bottom - Top;
    public bool StrictlyContains(int y) => Top < y && y < Bottom;
}

public readonly record struct Heading(int Y, int Level, string Text);

/// <summary>Everything the splitter needs to know about a page, in image pixels.</summary>
public sealed record PageLayout(
    int Height,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<Span> ContentZones,
    IReadOnlyList<Heading> Headings)
{
    public static PageLayout Empty(int height) => new(height, [], [], []);
}

/// <summary>One output image: rows [Top, Bottom) of the full page, plus the section heading it belongs to.</summary>
public readonly record struct Step(int Index, int Top, int Bottom, string? Heading)
{
    public int Height => Bottom - Top;
}
