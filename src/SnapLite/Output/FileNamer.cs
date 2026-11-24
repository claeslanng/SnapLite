using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SnapLite.Output;

/// <summary>
/// Turns a pattern like "SnapLite_{yyyy-MM-dd_HH-mm-ss}" into a safe file name.
/// {title} is the window/page title; any other {token} is a .NET date/time format.
/// </summary>
internal static partial class FileNamer
{
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[_\-\s]{2,}")]
    private static partial Regex RepeatedSeparators();

    public static string BuildBaseName(string pattern, DateTime now, string? title)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "SnapLite_{yyyy-MM-dd_HH-mm-ss}";

        string name = TokenRegex().Replace(pattern, m =>
        {
            string token = m.Groups[1].Value;
            if (token.Equals("title", StringComparison.OrdinalIgnoreCase))
                return Truncate(title ?? "", 60);
            try { return now.ToString(token, CultureInfo.InvariantCulture); }
            catch (FormatException) { return m.Value; }
        });

        name = Sanitize(name);
        // An empty {title} leaves "a__b"; tidy that up.
        name = RepeatedSeparators().Replace(name, s => s.Value[0].ToString()).Trim('_', '-', ' ', '.');
        return name.Length == 0 ? "SnapLite_" + now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) : name;
    }

    public static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
