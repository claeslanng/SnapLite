using System.Globalization;
using System.Text;

namespace SnapLite.Core.Splitting;

/// <summary>Builds the steps.md index that lists each step image with its section heading.</summary>
public static class StepsIndexWriter
{
    public static string Build(
        string title,
        string? url,
        DateTime captured,
        string mode,
        IReadOnlyList<Step> steps,
        IReadOnlyList<string> fileNames,
        string? fullImageFileName = null)
    {
        if (steps.Count != fileNames.Count)
            throw new ArgumentException("Need exactly one file name per step.", nameof(fileNames));

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Escape(string.IsNullOrWhiteSpace(title) ? "Captured page" : title)).AppendLine();
        if (!string.IsNullOrWhiteSpace(url)) sb.Append("Source: <").Append(url).AppendLine(">  ");
        sb.Append("Captured: ").Append(captured.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
          .Append(" · ").Append(steps.Count).Append(steps.Count == 1 ? " step" : " steps")
          .Append(" (").Append(mode).AppendLine(")");
        if (fullImageFileName != null)
            sb.AppendLine().Append("Full page: [").Append(Escape(fullImageFileName)).Append("](").Append(Link(fullImageFileName)).AppendLine(")");
        sb.AppendLine();

        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            string heading = string.IsNullOrWhiteSpace(s.Heading) ? $"Step {s.Index}" : s.Heading!;
            sb.Append("## ").Append(s.Index).Append(". ").AppendLine(Escape(heading)).AppendLine();
            sb.Append("![Step ").Append(s.Index).Append("](").Append(Link(fileNames[i])).AppendLine(")").AppendLine();
            sb.Append('_').Append(fileNames[i].Replace("_", "\\_")).Append(" · rows ")
              .Append(s.Top).Append('–').Append(s.Bottom).Append(" (").Append(s.Height).AppendLine(" px)_").AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Escapes characters that would turn heading text into Markdown syntax.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.ReplaceLineEndings(" "))
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Relative link target; spaces and parentheses are percent-encoded so the link survives.</summary>
    private static string Link(string fileName) =>
        fileName.Replace("%", "%25").Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
}
