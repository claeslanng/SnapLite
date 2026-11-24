using System.Reflection;

namespace SnapLite.WebCapture;

/// <summary>Loads the JavaScript files from /shared-scripts that are embedded in the exe.</summary>
internal static class PageScripts
{
    private static string? _cleanup, _sections;

    public static string Cleanup => _cleanup ??= Load("cleanup.js");

    /// <summary>Extends window.__snaplite with measureLayout(); inject after <see cref="Cleanup"/>.</summary>
    public static string Sections => _sections ??= Load("sections.js");

    private static string Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SnapLite.Scripts." + name)
                      ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
