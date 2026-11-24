using SnapLite.Capture;

namespace SnapLite.Settings;

internal enum ImageFormatKind { Png, Jpeg }

/// <summary>
/// User settings, stored as JSON in %APPDATA%\SnapLite\settings.json.
/// "Start with Windows" is not stored here: the HKCU Run key is the single source of truth.
/// </summary>
internal sealed class AppSettings
{
    public const string DefaultPattern = "SnapLite_{yyyy-MM-dd_HH-mm-ss}";

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapLite");

    public static IReadOnlyDictionary<CaptureAction, string> DefaultHotkeys { get; } = new Dictionary<CaptureAction, string>
    {
        [CaptureAction.Region] = "Ctrl+Shift+1",
        [CaptureAction.ActiveWindow] = "Ctrl+Shift+2",
        [CaptureAction.AllMonitors] = "Ctrl+Shift+3",
        [CaptureAction.CurrentMonitor] = "",
        [CaptureAction.WebFullPage] = "Ctrl+Shift+4",
        [CaptureAction.WebSteps] = "Ctrl+Shift+5",
        [CaptureAction.ScrollWindow] = "Ctrl+Shift+6",
    };

    public ScrollCapture.ScrollCaptureOptions ToScrollOptions() =>
        new(ScrollMaxHeight, ScrollStartFromTop, ScrollSettleDelayMs);

    public WebCapture.StepSettings ToStepSettings() =>
        new(StepsSmart, StepMinHeight, StepMaxHeight, SimpleStepHeight, SimpleStepOverlap);

    public static readonly int[] ViewportPresets = [1280, 1440, 1920, 390];

    public string SaveFolder { get; set; } = DefaultSaveFolder;

    /// <summary>Text in {braces} is a date/time format; {title} is the window/page title.</summary>
    public string FileNamePattern { get; set; } = DefaultPattern;

    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
    public int JpegQuality { get; set; } = 90;
    public bool CopyToClipboard { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public CaptureAction TrayLeftClick { get; set; } = CaptureAction.Region;

    /// <summary>Also bind the bare Print Screen key to region capture. Off by default (Windows uses it).</summary>
    public bool UsePrintScreen { get; set; }

    public Dictionary<CaptureAction, string> Hotkeys { get; set; } = new(DefaultHotkeys);

    // ---- Web capture defaults (the web dialog remembers the last choices here) ----
    public int WebViewportWidth { get; set; } = 1280;
    public bool WebHideBanners { get; set; } = true;
    public bool WebFixFloating { get; set; } = true;
    public bool WebDisableAnimations { get; set; } = true;
    public int WebSettleDelayMs { get; set; } = 800;
    public int WebMaxPageHeight { get; set; } = 30000;

    // ---- Steps mode ----
    public bool StepsSmart { get; set; } = true;
    public int StepMinHeight { get; set; } = 400;
    public int StepMaxHeight { get; set; } = 1600;
    /// <summary>Simple split chunk height; 0 = one viewport.</summary>
    public int SimpleStepHeight { get; set; } = 0;
    public int SimpleStepOverlap { get; set; } = 40;
    public bool StepsSaveFullImage { get; set; }
    public bool StepsWriteIndex { get; set; } = true;

    // ---- Scroll capture of any window ----
    public int ScrollMaxHeight { get; set; } = 20000;
    public bool ScrollStartFromTop { get; set; } = true;
    public int ScrollSettleDelayMs { get; set; } = 150;

    /// <summary>Fills gaps and clamps values after loading a possibly old or hand-edited file.</summary>
    public AppSettings Normalize()
    {
        ScrollMaxHeight = Math.Clamp(ScrollMaxHeight, 1000, 100000);
        ScrollSettleDelayMs = Math.Clamp(ScrollSettleDelayMs, 0, 3000);
        StepMaxHeight = Math.Clamp(StepMaxHeight, 300, 10000);
        StepMinHeight = Math.Clamp(StepMinHeight, 100, StepMaxHeight - 100);
        SimpleStepHeight = SimpleStepHeight <= 0 ? 0 : Math.Clamp(SimpleStepHeight, 200, 10000);
        SimpleStepOverlap = Math.Clamp(SimpleStepOverlap, 0, 500);
        WebViewportWidth = Math.Clamp(WebViewportWidth, 320, 3840);
        WebSettleDelayMs = Math.Clamp(WebSettleDelayMs, 0, 10000);
        WebMaxPageHeight = Math.Clamp(WebMaxPageHeight, 2000, 100000);
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder;
        if (string.IsNullOrWhiteSpace(FileNamePattern)) FileNamePattern = DefaultPattern;
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        if (!Enum.IsDefined(TrayLeftClick)) TrayLeftClick = CaptureAction.Region;

        Hotkeys ??= new();
        foreach (var (action, text) in DefaultHotkeys)
            Hotkeys.TryAdd(action, text); // actions added in newer versions get their default
        return this;
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Hotkeys = new Dictionary<CaptureAction, string>(Hotkeys);
        return c;
    }
}
