using System.Text;

namespace SnapLite.Interop;

/// <summary>
/// Tiny error log at %LOCALAPPDATA%\SnapLite\snaplite.log. Written only when something goes wrong
/// (nothing is logged while idle); kept under ~256 KB by rolling over to snaplite.old.log.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapLite");

    public static string FilePath => Path.Combine(Folder, "snaplite.log");

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    public static void Info(string message) => Write("INFO  " + message);

    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Folder, "snaplite.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{Version}  {text}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never cause a second failure */ }
    }

    public static string Version =>
        typeof(AppLog).Assembly.GetName().Version?.ToString(3) ?? "?";
}
