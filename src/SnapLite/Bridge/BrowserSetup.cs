using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SnapLite.Interop;
using SnapLite.Tray;

namespace SnapLite.Bridge;

/// <summary>
/// Installs the browser extension's files and registers SnapLite as its native messaging host.
/// Everything is per user (HKCU + %LOCALAPPDATA%); nothing needs admin rights.
/// </summary>
internal static class BrowserSetup
{
    public const string HostName = "com.snaplite.host";

    /// <summary>Fixed by the public key in extension/manifest.json ("key"); a unit test checks they match.</summary>
    public const string ExtensionId = "gfgeiefplicdmofnbiinlhlknjdcnkpl";

    /// <summary>
    /// IDs the Chrome Web Store / Edge Add-ons gave the published extension (stores assign their own).
    /// Add each one here when it's known; installed copies pick it up on their next start.
    /// </summary>
    public static readonly string[] StoreExtensionIds =
    [
        "cpllehgmchajmhgeimiciapplhgfhokj", // Chrome Web Store
    ];

    /// <summary>The extension's page in the Chrome Web Store (also installs in Edge and Brave).</summary>
    public const string ChromeWebStoreUrl = "https://chromewebstore.google.com/detail/snaplite/cpllehgmchajmhgeimiciapplhgfhokj";

    /// <summary>Every extension allowed to start SnapLite as its native messaging host.</summary>
    public static IEnumerable<string> AllowedExtensionIds => [ExtensionId, .. StoreExtensionIds];

    /// <summary>Where each Chromium browser looks for native messaging hosts (HKCU).</summary>
    private static readonly (string Browser, string Key)[] Browsers =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + HostName),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName),
        ("Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName),
        ("Chromium", @"Software\Chromium\NativeMessagingHosts\" + HostName),
    ];

    private const string ResourcePrefix = "SnapLite.Extension/";
    private const string VersionFile = ".snaplite-version";

    public static string ExtensionFolder => Path.Combine(AppLog.Folder, "browser-extension");
    public static string HostManifestPath => Path.Combine(AppLog.Folder, "native-host", HostName + ".json");

    public static bool IsSetUp
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Browsers[0].Key);
                return key != null && File.Exists(HostManifestPath) && File.Exists(Path.Combine(ExtensionFolder, "manifest.json"));
            }
            catch { return false; }
        }
    }

    /// <summary>Writes the extension folder and registers the host. Safe to run again (repair/update).</summary>
    public static void SetUp()
    {
        ExtractExtension();
        WriteHostManifest();
        foreach (var (_, keyPath) in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("", HostManifestPath);
        }
    }

    public static void Remove()
    {
        foreach (var (_, keyPath) in Browsers)
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        try { File.Delete(HostManifestPath); } catch { }
        try { Directory.Delete(ExtensionFolder, recursive: true); } catch { /* the browser may still have it open */ }
    }

    /// <summary>
    /// At startup: if set up, keep it pointing at this exe (it may have moved) and refresh the extension
    /// files after an update. Cheap: a file read and a compare.
    /// </summary>
    public static void Sync()
    {
        try
        {
            if (!IsSetUp) return;
            // Rewritten when the exe moved or the list of allowed extensions changed (e.g. a store ID was added).
            if (File.ReadAllText(HostManifestPath) != HostManifestJson()) WriteHostManifest();
            string stamp = Path.Combine(ExtensionFolder, VersionFile);
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != StampText()) ExtractExtension(ExtensionFolder, forStore: false);
        }
        catch (Exception ex) { AppLog.Error("browser setup sync", ex); }
    }

    private static string HostManifestJson() => new JsonObject
    {
        ["name"] = HostName,
        ["description"] = "SnapLite screenshot tool",
        ["path"] = Environment.ProcessPath,
        ["type"] = "stdio",
        ["allowed_origins"] = new JsonArray(AllowedExtensionIds.Select(id => (JsonNode)$"chrome-extension://{id}/").ToArray()),
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private static void WriteHostManifest()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HostManifestPath)!);
        File.WriteAllText(HostManifestPath, HostManifestJson());
    }

    /// <summary>
    /// <c>SnapLite.exe --export-extension &lt;folder&gt; [--store]</c>: writes the complete extension to a folder.
    /// With --store, the developer "key" is left out of manifest.json (the stores assign their own ID).
    /// </summary>
    public static int ExportCli(string[] args)
    {
        string folder = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "extension-export";
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        ExtractExtension(Path.GetFullPath(folder), forStore: args.Contains("--store"));
        return 0;
    }

    /// <summary>Extension files are embedded in the exe; the page scripts and icons are shared with the app.</summary>
    private static void ExtractExtension() => ExtractExtension(ExtensionFolder, forStore: false);

    private static void ExtractExtension(string root, bool forStore)
    {
        Directory.CreateDirectory(root);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            string? relative =
                name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ? name[ResourcePrefix.Length..] :
                name.StartsWith("SnapLite.Scripts.", StringComparison.Ordinal) ? "shared/" + name["SnapLite.Scripts.".Length..] :
                null;
            if (relative == null) continue;
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')));
            if (!target.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }

        Directory.CreateDirectory(Path.Combine(root, "icons"));
        foreach (int size in new[] { 16, 32, 48, 128 })
        {
            using var bmp = AppIcons.DrawBitmap(size);
            bmp.Save(Path.Combine(root, "icons", $"icon{size}.png"), ImageFormat.Png);
        }

        if (forStore)
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            var manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest.Remove("key");
            File.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            File.WriteAllText(Path.Combine(root, VersionFile), StampText());
        }
    }

    private static string StampText() =>
        AppLog.Version + " " + File.GetLastWriteTimeUtc(Environment.ProcessPath ?? "").Ticks;

    /// <summary>Opens the browser's extensions page (needs the browser's exe; chrome:// links can't be opened by Windows).</summary>
    public static bool OpenExtensionsPage(string browser)
    {
        var (exe, url) = browser == "Edge" ? ("msedge.exe", "edge://extensions") : ("chrome.exe", "chrome://extensions");
        string? path = AppPath(exe);
        if (path == null) return false;
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Process.Start(new ProcessStartInfo(path, url) { UseShellExecute = false })?.Dispose();
        return true;
    }

    public static bool IsInstalled(string browser) => AppPath(browser == "Edge" ? "msedge.exe" : "chrome.exe") != null;

    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        return null;
    }
}
