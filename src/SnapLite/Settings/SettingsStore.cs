using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapLite.Settings;

/// <summary>Loads/saves settings.json. Uses source-generated JSON (no reflection, fast startup).</summary>
internal static class SettingsStore
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLite");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    // Relaxed escaping keeps "Ctrl+Shift+1" readable instead of "Ctrl+Shift+1".
    private static readonly SettingsJsonContext Json = new(new JsonSerializerOptions(SettingsJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>
    /// Never throws. A missing file gives defaults (and <paramref name="firstRun"/> = true).
    /// A broken file is kept as settings.bad.json and defaults are used, with a warning.
    /// </summary>
    public static AppSettings Load(out bool firstRun, out string? warning)
    {
        firstRun = false;
        warning = null;

        if (!File.Exists(FilePath))
        {
            firstRun = true;
            var defaults = new AppSettings().Normalize();
            TrySave(defaults); // so the user can find and edit the file
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize(json, Json.AppSettings)
                    ?? throw new JsonException("File is empty.");
            return s.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string backup = Path.Combine(Folder, "settings.bad.json");
            try { File.Copy(FilePath, backup, overwrite: true); } catch { /* best effort */ }
            warning = $"settings.json could not be read ({ex.Message}). Using defaults; the old file was kept as settings.bad.json.";
            return new AppSettings().Normalize();
        }
    }

    /// <summary>Atomic save: write a temp file, then replace.</summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json.AppSettings));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static void TrySave(AppSettings s)
    {
        try { Save(s); } catch { /* read-only profile etc. — keep running with in-memory settings */ }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
