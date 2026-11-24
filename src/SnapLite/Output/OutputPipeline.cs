using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SnapLite.Interop;
using SnapLite.Settings;

namespace SnapLite.Output;

/// <summary>Copies a capture to the clipboard and saves it to disk.</summary>
internal sealed class OutputPipeline(Func<AppSettings> settings)
{
    /// <param name="Path">The saved file.</param>
    /// <param name="Copied">The image is on the clipboard.</param>
    /// <param name="Note">Something the user should know (clipboard busy, saved somewhere else…).</param>
    public sealed record Saved(string Path, bool Copied, string? Note);

    /// <summary>Must be called on the UI (STA) thread.</summary>
    public async Task<Saved> HandleAsync(Bitmap image, string? title)
    {
        AppSettings s = settings();
        DateTime now = DateTime.Now;
        var notes = new List<string>();

        // Encode off the UI thread; the PNG bytes are reused for the clipboard.
        byte[] png = await Task.Run(() => Encode(image, ImageFormatKind.Png, 0));

        bool copied = false;
        if (s.CopyToClipboard)
        {
            try { SetClipboard(image, png); copied = true; }
            catch (ExternalException ex)
            {
                AppLog.Error("clipboard", ex);
                notes.Add("Couldn't copy to the clipboard (another app is using it)");
            }
        }

        byte[] fileBytes = s.ImageFormat == ImageFormatKind.Png
            ? png
            : await Task.Run(() => Encode(image, ImageFormatKind.Jpeg, s.JpegQuality));

        string ext = s.ImageFormat == ImageFormatKind.Png ? ".png" : ".jpg";
        string baseName = FileNamer.BuildBaseName(s.FileNamePattern, now, title);
        var (path, fallbackNote) = await WithFallbackFolderAsync(s.SaveFolder,
            folder => WriteUniqueAsync(folder, baseName, ext, fileBytes));
        if (fallbackNote != null) notes.Add(fallbackNote);
        return new Saved(path, copied, notes.Count > 0 ? string.Join(" · ", notes) : null);
    }

    /// <summary>
    /// Runs <paramref name="save"/> in the chosen folder; if that folder can't be used (unplugged drive,
    /// no permission, invalid path) it saves to Pictures\SnapLite instead and says so.
    /// </summary>
    public static async Task<(T Result, string? Note)> WithFallbackFolderAsync<T>(string folder, Func<string, Task<T>> save)
    {
        try
        {
            return (await save(folder), null);
        }
        catch (Exception ex) when (IsFolderProblem(ex) && !SameFolder(folder, AppSettings.DefaultSaveFolder))
        {
            AppLog.Error("save to " + folder, ex);
            var result = await save(AppSettings.DefaultSaveFolder);
            return (result, $"Couldn't use {folder}; saved to Pictures\\SnapLite instead");
        }
    }

    private static bool IsFolderProblem(Exception ex) =>
        ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException or ArgumentException or NotSupportedException;

    private static bool SameFolder(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static byte[] Encode(Bitmap image, ImageFormatKind format, int jpegQuality)
    {
        using var ms = new MemoryStream();
        if (format == ImageFormatKind.Jpeg)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            image.Save(ms, codec, ep);
        }
        else
        {
            image.Save(ms, ImageFormat.Png);
        }
        return ms.ToArray();
    }

    /// <summary>Puts a saved capture back on the clipboard (the toast's Copy button).</summary>
    public static void CopyFileToClipboard(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var bmp = new Bitmap(new MemoryStream(bytes));
        byte[] png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? bytes : Encode(bmp, ImageFormatKind.Png, 0);
        SetClipboard(bmp, png);
    }

    /// <summary>
    /// Puts both a bitmap (for classic apps) and a "PNG" stream (for browsers, chat apps,
    /// Office) on the clipboard. copy:true renders it now, so the bitmap can be disposed after.
    /// </summary>
    private static void SetClipboard(Bitmap image, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", false, new MemoryStream(png));
        Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
    }

    private static async Task<string> WriteUniqueAsync(string folder, string baseName, string ext, byte[] data)
    {
        Directory.CreateDirectory(folder);
        for (int i = 1; ; i++)
        {
            string path = Path.Combine(folder, (i == 1 ? baseName : $"{baseName}_{i}") + ext);
            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }
            catch (IOException) when (i < 1000 && File.Exists(path))
            {
                continue; // name taken (two captures in the same second): try the next suffix
            }
            try
            {
                await using (fs) await fs.WriteAsync(data);
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch { } // no half-written files (e.g. disk full)
                throw;
            }
        }
    }
}
