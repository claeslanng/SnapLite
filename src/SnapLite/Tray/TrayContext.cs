using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapLite.Bridge;
using SnapLite.Capture;
using SnapLite.Core.Splitting;
using SnapLite.Hotkeys;
using SnapLite.Interop;
using SnapLite.Output;
using SnapLite.ScrollCapture;
using SnapLite.Settings;
using SnapLite.WebCapture;

namespace SnapLite.Tray;

/// <summary>
/// The whole app while idle: a tray icon, a context menu and a message-only window
/// for hotkeys. No timers, no polling — everything is driven by Windows messages.
/// </summary>
internal sealed class TrayContext : ApplicationContext, ISettingsHost
{
    private AppSettings _settings;
    private readonly bool _firstRun;
    private readonly string? _settingsWarning;
    private SettingsForm? _settingsForm;
    private bool _hotkeysSuspended;
    private readonly MessageWindow _messageWindow = new();
    private readonly HotkeyManager _hotkeys;
    private readonly OutputPipeline _output;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconNormal = AppIcons.Create(paused: false);
    private readonly Icon _iconPaused = AppIcons.Create(paused: true);
    private readonly Dictionary<CaptureAction, ToolStripMenuItem> _captureItems = new();
    private ToolStripMenuItem? _pauseItem;
    private bool _paused;

    private bool _busy;
    private ToastForm? _toast;
    private BridgeServer? _bridge;

    /// <summary>The running instance (for reporting unexpected errors from Program).</summary>
    public static TrayContext? Current { get; private set; }

    public TrayContext()
    {
        Current = this;
        _settings = SettingsStore.Load(out _firstRun, out _settingsWarning);
        _output = new OutputPipeline(() => _settings);
        _hotkeys = new HotkeyManager(_messageWindow);
        _hotkeys.Pressed += action => RunCapture(action, fromUi: false);
        _messageWindow.MessageReceived += (msg, _) =>
        {
            if (msg == Native.WM_SNAPLITE_SHOW) OpenSettings(); // a second launch shows this instance
        };

        // Hotkeys first: they are what the user is waiting for.
        RegisterHotkeys(reportFailures: false);

        _tray = new NotifyIcon { Icon = _iconNormal, Text = "SnapLite", Visible = true };
        _tray.MouseDown += (_, e) =>
        {
            // NotifyIcon shows its menu on mouse-up, so building it here still opens it on this click.
            if (e.Button == MouseButtons.Right) EnsureMenu();
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RunCapture(_settings.TrayLeftClick, fromUi: true);
        };

        // Building a ToolStrip the first time costs ~0.8 s, so do it after startup,
        // when the message loop first goes idle (one-shot, not a timer), then trim memory.
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        StartupRegistration.SyncPath(); // keep autostart working if the exe was moved

        if (_settingsWarning != null) ShowWarning("Settings were reset", _settingsWarning);
        else if (_lastHotkeyFailures.Count > 0) ReportHotkeyFailures(_lastHotkeyFailures);
        else if (_firstRun) ShowInfo("SnapLite is running in the tray", WelcomeText());

        EnsureMenu();

        // The browser extension's way in (a named pipe; costs nothing while idle).
        BrowserSetup.Sync();
        _bridge = new BridgeServer(HandleBridgeAsync);
        _bridge.Start();

        MemoryTrimmer.Trim();
    }

    private string WelcomeText()
    {
        var lines = new List<string>();
        foreach (var (action, text) in _settings.Hotkeys)
            if (HotkeyBinding.TryParse(text, out var b)) lines.Add($"{b}  {action.DisplayName()}");
        lines.Add("Right-click the icon for more.");
        return string.Join("\n", lines);
    }

    // ---- Settings ---------------------------------------------------------------

    private void OpenSettings()
    {
        EnsureMenu();
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(this, _settings.Clone(), AppIcons.Create(paused: false, pixelSize: 32));
            _settingsForm.FormClosed += (_, _) =>
            {
                _settingsForm?.Icon?.Dispose();
                _settingsForm?.Dispose();
                _settingsForm = null;
                MemoryTrimmer.Trim(); // the window is gone; give its memory back
            };
        }
        if (!_settingsForm.Visible) _settingsForm.Show();
        if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
        Native.SetForegroundWindow(_settingsForm.Handle);
    }

    bool ISettingsHost.IsHotkeyAvailable(HotkeyBinding binding) => _hotkeys.IsAvailable(binding);

    void ISettingsHost.SuspendHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        _hotkeys.UnregisterAll();
    }

    void ISettingsHost.ResumeHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        if (!_paused) RegisterHotkeys(reportFailures: false);
    }

    IReadOnlyList<string> ISettingsHost.ApplySettings(AppSettings settings, bool startWithWindows)
    {
        SettingsStore.Save(settings); // throws on I/O errors; the form reports it
        _settings = settings;

        var problems = new List<string>();
        try { StartupRegistration.Set(startWithWindows); }
        catch (Exception ex) { problems.Add($"Start with Windows: {ex.Message}"); }

        _hotkeysSuspended = false;
        if (_paused) UpdateMenuShortcuts();
        else
        {
            RegisterHotkeys(reportFailures: false);
            problems.AddRange(_lastHotkeyFailures);
        }
        return problems;
    }

    private void EnsureMenu()
    {
        if (_tray.ContextMenuStrip != null) return;

        var menu = new ContextMenuStrip();
        foreach (var action in new[] { CaptureAction.Region, CaptureAction.ActiveWindow, CaptureAction.CurrentMonitor, CaptureAction.AllMonitors, CaptureAction.ScrollWindow, CaptureAction.WebFullPage, CaptureAction.WebSteps })
        {
            if (action == CaptureAction.WebFullPage) menu.Items.Add(new ToolStripSeparator());
            var item = new ToolStripMenuItem(action.DisplayName(), null, (_, _) => RunCapture(action, fromUi: true));
            _captureItems[action] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open captures folder", null, (_, _) => OpenCapturesFolder());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _pauseItem = new ToolStripMenuItem("Pause hotkeys", null, (_, _) => SetPaused(!_paused)) { Checked = _paused };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray.ContextMenuStrip = menu;
        UpdateMenuShortcuts();
    }

    // ---- Hotkeys ----------------------------------------------------------------

    private IReadOnlyList<string> _lastHotkeyFailures = [];

    private void RegisterHotkeys(bool reportFailures)
    {
        _lastHotkeyFailures = _hotkeys.RegisterAll(EffectiveBindings());
        UpdateMenuShortcuts();
        if (reportFailures) ReportHotkeyFailures(_lastHotkeyFailures);
    }

    private IEnumerable<(CaptureAction, string)> EffectiveBindings()
    {
        foreach (var (action, text) in _settings.Hotkeys) yield return (action, text);
        if (_settings.UsePrintScreen) yield return (CaptureAction.Region, "PrintScreen");
    }

    private void ReportHotkeyFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count > 0)
            ShowWarning("Some shortcuts could not be registered", string.Join("\n", failures));
    }

    private void UpdateMenuShortcuts()
    {
        foreach (var (action, item) in _captureItems)
        {
            _settings.Hotkeys.TryGetValue(action, out var text);
            item.ShortcutKeyDisplayString = HotkeyBinding.TryParse(text, out var b) ? b.ToString() : null;
        }
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        if (_pauseItem != null) _pauseItem.Checked = paused;
        _tray.Icon = paused ? _iconPaused : _iconNormal;
        _tray.Text = paused ? "SnapLite (hotkeys paused)" : "SnapLite";
        if (paused) _hotkeys.UnregisterAll();
        else RegisterHotkeys(reportFailures: true);
    }

    // ---- Capture ----------------------------------------------------------------

    private async void RunCapture(CaptureAction action, bool fromUi)
    {
        if (_busy) return;
        _busy = true;
        Bitmap? image = null;
        try
        {
            // Let the tray menu / tooltip disappear so it isn't in the picture.
            if (fromUi) await Task.Delay(200);

            string? title = null, note = null;
            switch (action)
            {
                case CaptureAction.Region:
                    image = RegionOverlayForm.SelectRegion();
                    break;
                case CaptureAction.ActiveWindow:
                    var result = WindowCapture.CaptureActiveWindow();
                    if (result is { } r) { image = r.Image; title = r.Title; }
                    else ShowWarning("No window to capture", "Couldn't find an active window.");
                    break;
                case CaptureAction.CurrentMonitor:
                    image = ScreenCapture.CaptureCurrentMonitor();
                    break;
                case CaptureAction.AllMonitors:
                    image = ScreenCapture.CaptureAllMonitors();
                    break;
                case CaptureAction.WebFullPage:
                    var web = RunWebDialog();
                    if (web != null)
                    {
                        image = web.Image;
                        title = web.Title;
                        if (web.Truncated) note = $"Page cut at {_settings.WebMaxPageHeight:N0} px";
                    }
                    break;
                case CaptureAction.WebSteps:
                    await RunStepsAsync(); // saves its own set of files
                    return;
                case CaptureAction.ScrollWindow:
                    IntPtr target = ScrollCaptureController.PickTarget(underCursor: !fromUi);
                    if (target == IntPtr.Zero) { ShowWarning("No window to capture", "Point at a window and press the shortcut again."); return; }
                    var scrolled = await ScrollCaptureController.RunAsync(target, _settings.ToScrollOptions());
                    if (scrolled?.Image == null)
                    {
                        ShowWarning("Scroll capture", scrolled?.Note ?? "That window is too small to capture.");
                        return;
                    }
                    image = scrolled.Image; title = scrolled.Title; note = scrolled.Note;
                    break;
            }

            if (image == null) return; // cancelled

            var saved = await _output.HandleAsync(image, title);
            string? notes = string.Join(" · ", new[] { note, saved.Note }.Where(n => n != null));
            if (_settings.ShowNotifications) ShowSaved(saved, image, notes);
            else if (saved.Note != null) ShowWarning("Saved, with a problem", saved.Note);
        }
        catch (Exception ex)
        {
            AppLog.Error($"capture ({action})", ex);
            ShowWarning("Capture failed", ex.Message);
        }
        finally
        {
            image?.Dispose();
            _busy = false;
            MemoryTrimmer.Trim();
        }
    }

    /// <summary>Shows the full-page web capture dialog.</summary>
    private WebPageCapturer.Result? RunWebDialog()
    {
        using var icon = AppIcons.Create(paused: false, pixelSize: 32);
        using var dlg = new WebCaptureDialog(_settings, icon);
        if (dlg.ShowDialog() != DialogResult.OK || dlg.Result == null) return null;
        RememberWebChoices(dlg.UsedOptions, smart: null);
        return dlg.Result;
    }

    /// <summary>Steps mode: dialog → capture → numbered images in a subfolder (+ steps.md).</summary>
    private async Task RunStepsAsync()
    {
        WebPageCapturer.StepsResult? r;
        using (var icon = AppIcons.Create(paused: false, pixelSize: 32))
        using (var dlg = new WebCaptureDialog(_settings, icon, stepsMode: true))
        {
            if (dlg.ShowDialog() != DialogResult.OK || dlg.StepsResult == null) return;
            r = dlg.StepsResult;
            RememberWebChoices(dlg.UsedOptions, dlg.UsedSmartSplit);
        }

        using (r.FullImage)
            await SaveStepsAsync(r.FullImage, r.Steps, r.Title, r.FinalUrl, r.Smart,
                r.Truncated ? $"Page cut at {_settings.WebMaxPageHeight:N0} px" : null);
    }

    /// <summary>Writes a steps folder and shows the notification (desktop web capture and browser extension).</summary>
    private async Task<StepsWriter.Saved> SaveStepsAsync(Bitmap full, IReadOnlyList<Step> steps, string title, string? url, bool smart, string? note)
    {
        var now = DateTime.Now;
        var (saved, fallbackNote) = await OutputPipeline.WithFallbackFolderAsync(_settings.SaveFolder,
            folder => StepsWriter.SaveAsync(full, steps, title, url, smart, _settings, folder, now));
        if (_settings.ShowNotifications)
        {
            var actions = new List<ToastAction> { new("Open folder", () => { OpenFile(saved.Folder); return null; }) };
            if (saved.IndexPath is { } index) actions.Add(new("Open steps.md", () => { OpenFile(index); return null; }));
            ShowToast($"Saved {saved.Count} steps", [Path.GetFileName(saved.Folder), note, fallbackNote],
                full, actions, () => OpenFile(saved.Folder));
        }
        else if (fallbackNote != null) ShowWarning("Saved, with a problem", fallbackNote);
        return saved;
    }

    // ---- Browser extension ------------------------------------------------------

    /// <summary>
    /// A capture from the browser extension (relayed by the native host): an image file in the incoming
    /// folder plus what the page told us. Saved, copied and announced like any other capture.
    /// </summary>
    private async Task<JsonObject> HandleBridgeAsync(JsonObject req)
    {
        string cmd = (string?)req["cmd"] ?? "";
        if (cmd == "ping") return new JsonObject { ["ok"] = true };
        if (cmd != "import") throw new InvalidDataException($"Unknown command \"{cmd}\".");

        string file = (string?)req["file"] ?? "";
        if (!BridgeClient.IsInIncomingFolder(file) || !File.Exists(file))
            throw new InvalidDataException($"The image from the browser wasn't found ({file}).");
        try
        {
            using var image = ImageConvert.LoadFile(file);
            string title = (string?)req["title"] is { Length: > 0 } t ? t : "Webpage";
            string? url = (string?)req["url"];
            double scale = (double?)req["scale"] is > 0 and var s ? s : 1.0;
            string? note = (bool?)req["truncated"] == true ? "Page cut at the maximum height" : null;

            if ((string?)req["kind"] == "steps")
            {
                // Without a layout (shouldn't happen) still save steps: one per screen.
                PageLayout layout = PageLayout.Empty(image.Height);
                if (req["layout"] is JsonObject layoutJson)
                {
                    using var doc = JsonDocument.Parse(layoutJson.ToJsonString());
                    layout = PageLayoutJson.Parse(doc.RootElement, scale) with { Height = image.Height };
                }
                bool smart = ((bool?)req["smart"] ?? _settings.StepsSmart) && req["layout"] is JsonObject;
                int viewport = (int)Math.Round(((double?)req["viewportHeight"] ?? 900) * scale);
                int chunk = _settings.SimpleStepHeight > 0 ? (int)Math.Round(_settings.SimpleStepHeight * scale) : viewport;
                var steps = smart
                    ? StepSplitter.SmartSplit(layout, (int)(_settings.StepMinHeight * scale), (int)(_settings.StepMaxHeight * scale))
                    : StepSplitter.SimpleSplit(image.Height, chunk, (int)(_settings.SimpleStepOverlap * scale), layout.Headings);
                var saved = await SaveStepsAsync(image, steps, title, url, smart, note);
                return new JsonObject { ["ok"] = true, ["folder"] = saved.Folder, ["count"] = saved.Count };
            }

            var result = await _output.HandleAsync(image, title);
            string notes = string.Join(" · ", new[] { note, result.Note }.Where(n => n != null));
            if (_settings.ShowNotifications) ShowSaved(result, image, notes);
            else if (result.Note != null) ShowWarning("Saved, with a problem", result.Note);
            return new JsonObject { ["ok"] = true, ["path"] = result.Path, ["copied"] = result.Copied };
        }
        finally
        {
            try { File.Delete(file); } catch { }
            MemoryTrimmer.Trim();
        }
    }

    /// <summary>The web dialogs remember the viewport, clean-up toggles and split mode the user picked.</summary>
    private void RememberWebChoices(WebCaptureOptions? o, bool? smart)
    {
        if (o == null) return;
        bool changed = o.ViewportWidth != _settings.WebViewportWidth || o.HideBanners != _settings.WebHideBanners ||
                       o.FixFloating != _settings.WebFixFloating || o.DisableAnimations != _settings.WebDisableAnimations ||
                       (smart.HasValue && smart.Value != _settings.StepsSmart);
        if (!changed) return;

        var s = _settings.Clone();
        s.WebViewportWidth = o.ViewportWidth;
        s.WebHideBanners = o.HideBanners;
        s.WebFixFloating = o.FixFloating;
        s.WebDisableAnimations = o.DisableAnimations;
        if (smart.HasValue) s.StepsSmart = smart.Value;
        try { SettingsStore.Save(s); _settings = s; } catch { /* not worth interrupting the capture */ }
    }

    // ---- Notifications ----------------------------------------------------------

    private void ShowSaved(OutputPipeline.Saved saved, Bitmap image, string? note)
    {
        string path = saved.Path;
        ShowToast($"Saved {image.Width} × {image.Height}",
            [Path.GetFileName(path), saved.Copied ? "Copied to clipboard" : null, string.IsNullOrEmpty(note) ? null : note],
            image,
            [
                new("Open", () => { OpenFile(path); return null; }),
                new("Show in folder", () => { ShowInFolder(path); return null; }),
                new("Copy", () => { OutputPipeline.CopyFileToClipboard(path); return "Copied ✓"; }),
            ],
            () => OpenFile(path));
    }

    /// <summary>One toast at a time: a new capture replaces the previous toast.</summary>
    private void ShowToast(string title, string?[] lines, Image? thumbnail, IReadOnlyList<ToastAction> actions, Action? onBodyClick)
    {
        try
        {
            _toast?.Close();
            var toast = new ToastForm(title, lines, thumbnail, actions, onBodyClick);
            toast.FormClosed += (_, _) =>
            {
                if (_toast == toast) _toast = null;
                toast.Dispose();
                if (!_busy) MemoryTrimmer.Trim();
            };
            _toast = toast;
            toast.Show();
        }
        catch (Exception ex)
        {
            // Never lose the "saved" message because of the toast itself.
            AppLog.Error("toast", ex);
            _tray.ShowBalloonTip(4000, title, string.Join("\n", lines.Where(l => l != null)), ToolTipIcon.None);
        }
    }

    private void ShowInfo(string title, string text) => _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    private void ShowWarning(string title, string text) => _tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning);

    /// <summary>Called by Program for exceptions nothing else caught.</summary>
    public static void ReportUnexpected(Exception ex)
    {
        if (Current is { } c)
            c.ShowWarning("Something went wrong", $"{ex.Message}\nDetails: {AppLog.FilePath}");
        else
            MessageBox.Show($"{ex.Message}\n\nDetails: {AppLog.FilePath}", "SnapLite error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---- Shell helpers ----------------------------------------------------------

    private void OpenCapturesFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            Process.Start(new ProcessStartInfo(_settings.SaveFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowWarning("Couldn't open folder", ex.Message); }
    }

    private void OpenFile(string path) // works for folders too
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY); // let the viewer come to the front
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowWarning("Couldn't open file", ex.Message); }
    }

    private void ShowInFolder(string path)
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { ShowWarning("Couldn't open folder", ex.Message); }
    }

    // ---- Lifetime ---------------------------------------------------------------

    protected override void ExitThreadCore()
    {
        _bridge?.Dispose();
        _toast?.Close();
        _settingsForm?.Close();
        _hotkeys.Dispose();
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeys.Dispose();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _messageWindow.Dispose();
            _iconNormal.Dispose();
            _iconPaused.Dispose();
        }
        base.Dispose(disposing);
    }
}
