using System.Drawing.Imaging;
using System.Globalization;
using SnapLite.Core.Splitting;
using SnapLite.Settings;

namespace SnapLite.Output;

/// <summary>
/// Saves a Steps capture: a subfolder "PageTitle_yyyy-MM-dd_HH-mm-ss" containing PageTitle_01.png,
/// PageTitle_02.png… plus optionally the full image and a steps.md index.
/// </summary>
internal static class StepsWriter
{
    public sealed record Saved(string Folder, int Count, string? IndexPath);

    /// <param name="baseFolder">Where the subfolder is created (normally the save folder).</param>
    public static Task<Saved> SaveAsync(Bitmap full, IReadOnlyList<Step> steps, string title, string? url, bool smart,
        AppSettings s, string baseFolder, DateTime now) =>
        Task.Run(() => Save(full, steps, title, url, smart, s, baseFolder, now));

    private static Saved Save(Bitmap full, IReadOnlyList<Step> steps, string title, string? url, bool smart,
        AppSettings s, string baseFolder, DateTime now)
    {
        string name = SafeTitle(title);
        string folder = UniqueFolder(Path.Combine(baseFolder, $"{name}_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}"));
        Directory.CreateDirectory(folder);

        string ext = s.ImageFormat == ImageFormatKind.Png ? ".png" : ".jpg";
        int digits = Math.Max(2, steps.Count.ToString(CultureInfo.InvariantCulture).Length);
        var files = new List<string>(steps.Count);

        foreach (var step in steps)
        {
            var rect = new Rectangle(0, step.Top, full.Width, Math.Min(step.Height, full.Height - step.Top));
            if (rect.Height <= 0) continue;
            using var part = full.Clone(rect, PixelFormat.Format32bppRgb);
            part.SetResolution(96, 96);
            string file = $"{name}_{step.Index.ToString("D" + digits, CultureInfo.InvariantCulture)}{ext}";
            File.WriteAllBytes(Path.Combine(folder, file), OutputPipeline.Encode(part, s.ImageFormat, s.JpegQuality));
            files.Add(file);
        }

        string? fullFile = null;
        if (s.StepsSaveFullImage)
        {
            fullFile = $"{name}_full{ext}";
            File.WriteAllBytes(Path.Combine(folder, fullFile), OutputPipeline.Encode(full, s.ImageFormat, s.JpegQuality));
        }

        string? indexPath = null;
        if (s.StepsWriteIndex && files.Count == steps.Count)
        {
            indexPath = Path.Combine(folder, "steps.md");
            File.WriteAllText(indexPath, StepsIndexWriter.Build(title, url, now, smart ? "smart split" : "simple split", steps, files, fullFile));
        }
        return new Saved(folder, files.Count, indexPath);
    }

    private static string SafeTitle(string title)
    {
        string t = FileNamer.Sanitize(title).Trim().Trim('.', ' ');
        if (t.Length > 50) t = t[..50].TrimEnd();
        return t.Length == 0 ? "Webpage" : t;
    }

    private static string UniqueFolder(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 2; ; i++)
            if (!Directory.Exists($"{path}_{i}")) return $"{path}_{i}";
    }
}
