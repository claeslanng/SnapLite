using System.Text.Json;

namespace SnapLite.Core.Splitting;

/// <summary>Reads the result of sections.js <c>measureLayout()</c> into a <see cref="PageLayout"/>.</summary>
public static class PageLayoutJson
{
    /// <param name="m">{ height, boundaries: [[y, kind]], headings: [[y, level, text]], zones: [[top, bottom]] } in CSS px.</param>
    /// <param name="scale">Image pixels per CSS pixel (the browser's device pixel ratio; 1 in the desktop app).</param>
    public static PageLayout Parse(JsonElement m, double scale = 1)
    {
        int S(JsonElement v) => (int)Math.Round(v.GetDouble() * scale);

        var boundaries = new List<Boundary>();
        foreach (var b in m.GetProperty("boundaries").EnumerateArray())
            boundaries.Add(new Boundary(S(b[0]), (BoundaryKind)b[1].GetInt32()));
        var headings = new List<Heading>();
        foreach (var h in m.GetProperty("headings").EnumerateArray())
            headings.Add(new Heading(S(h[0]), h[1].GetInt32(), h[2].GetString() ?? ""));
        var zones = new List<Span>();
        foreach (var z in m.GetProperty("zones").EnumerateArray())
            zones.Add(new Span(S(z[0]), S(z[1])));
        return new PageLayout(S(m.GetProperty("height")), boundaries, zones, headings);
    }
}
