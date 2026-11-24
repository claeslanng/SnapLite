using System.Diagnostics;
using SnapLite.Capture;
using SnapLite.Hotkeys;
using SnapLite.Interop;
using SnapLite.Output;

namespace SnapLite.Settings;

/// <summary>What the settings window needs from the running app.</summary>
internal interface ISettingsHost
{
    bool IsHotkeyAvailable(HotkeyBinding binding);
    /// <summary>Release global hotkeys while a shortcut box is focused, so pressing them records instead of captures.</summary>
    void SuspendHotkeys();
    void ResumeHotkeys();
    /// <summary>Saves and applies. Returns problems to show the user (empty = all good). May throw on I/O errors.</summary>
    IReadOnlyList<string> ApplySettings(AppSettings settings, bool startWithWindows);
}

/// <summary>
/// Settings window, built in code (no designer). It edits a copy of the settings;
/// nothing changes until Save. Minimizing hides it to the tray; closing discards it.
/// </summary>
internal sealed class SettingsForm : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly CaptureAction[] Actions = Enum.GetValues<CaptureAction>();
    private static readonly Color Ok = Color.FromArgb(16, 124, 16);
    private static readonly Color Warn = Color.FromArgb(157, 93, 0);
    private static readonly Color Bad = Color.FromArgb(196, 43, 28);

    private readonly ISettingsHost _host;
    private readonly bool _skipInstallPrompt; // not shown in the form; kept as it was

    // General
    private readonly CheckBox _startWithWindows = Check("Start SnapLite when Windows starts");
    private readonly Label _startupHint = Hint();
    private readonly ComboBox _trayClick = Combo(200);
    private readonly CheckBox _clipboard = Check("Copy each capture to the clipboard");
    private readonly CheckBox _notify = Check("Show a notification after each capture");

    // Saving
    private readonly TextBox _folder = new() { Width = 300 };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    private readonly TextBox _pattern = new() { Width = 300 };
    private readonly Label _patternPreview = Hint();
    private readonly ComboBox _format = Combo(100);
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Width = 60 };

    // Shortcuts
    private readonly Dictionary<CaptureAction, HotkeyBox> _boxes = new();
    private readonly Dictionary<CaptureAction, Label> _status = new();
    private readonly CheckBox _printScreen = Check("Also use the Print Screen key for region capture");
    private readonly Label _printScreenStatus = Hint();

    // Web capture
    private readonly ComboBox _webViewport = Combo(200);
    private readonly CheckBox _webBanners = Check("Hide cookie banners and pop-ups");
    private readonly CheckBox _webFloating = Check("Fix sticky headers and floating widgets");
    private readonly CheckBox _webAnimations = Check("Disable animations");
    private readonly NumericUpDown _webSettle = new() { Minimum = 0, Maximum = 10000, Increment = 100, Width = 80 };
    private readonly NumericUpDown _webMaxHeight = new() { Minimum = 2000, Maximum = 100000, Increment = 1000, Width = 80, ThousandsSeparator = true };

    // Steps
    private readonly ComboBox _stepsMode = Combo(220);
    private readonly NumericUpDown _stepMin = new() { Minimum = 100, Maximum = 9900, Increment = 50, Width = 80 };
    private readonly NumericUpDown _stepMax = new() { Minimum = 300, Maximum = 10000, Increment = 100, Width = 80 };
    private readonly NumericUpDown _simpleHeight = new() { Minimum = 0, Maximum = 10000, Increment = 100, Width = 80 };
    private readonly NumericUpDown _simpleOverlap = new() { Minimum = 0, Maximum = 500, Increment = 10, Width = 80 };
    private readonly CheckBox _stepsFull = Check("Also save the full page image");
    private readonly CheckBox _stepsIndex = Check("Write a steps.md index (each image with its section heading)");

    // Scrolling window
    private readonly NumericUpDown _scrollMax = new() { Minimum = 1000, Maximum = 100000, Increment = 1000, Width = 80, ThousandsSeparator = true };
    private readonly NumericUpDown _scrollDelay = new() { Minimum = 0, Maximum = 3000, Increment = 50, Width = 80 };
    private readonly CheckBox _scrollTop = Check("Scroll to the top first");

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Button _save = new() { Text = "Save", AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
    private readonly Button _reset = new() { Text = "Reset to defaults", AutoSize = true };

    public SettingsForm(ISettingsHost host, AppSettings current, Icon icon)
    {
        _host = host;
        _skipInstallPrompt = current.SkipInstallPrompt;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = "SnapLite Settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 430);
        AcceptButton = _save;
        CancelButton = _cancel;

        foreach (var a in Actions)
        {
            _trayClick.Items.Add(new Choice<CaptureAction>(a, a.DisplayName()));
            var box = new HotkeyBox { Width = 170 };
            box.BindingChanged += (_, _) => ValidateHotkeys();
            box.Enter += (_, _) => _host.SuspendHotkeys();
            box.Leave += (_, _) => _host.ResumeHotkeys();
            _boxes[a] = box;
            _status[a] = Hint();
        }
        _format.Items.Add(new Choice<ImageFormatKind>(ImageFormatKind.Png, "PNG (lossless)"));
        _format.Items.Add(new Choice<ImageFormatKind>(ImageFormatKind.Jpeg, "JPEG"));
        foreach (int w in AppSettings.ViewportPresets)
            _webViewport.Items.Add(new Choice<int>(w, w == 390 ? "Mobile — 390 px" : $"{w} px"));

        _tabs.TabPages.Add(BuildGeneralPage());
        _tabs.TabPages.Add(BuildSavingPage());
        _tabs.TabPages.Add(BuildShortcutsPage());
        _tabs.TabPages.Add(BuildWebPage());
        _stepsMode.Items.Add(new Choice<bool>(true, "Smart — by sections"));
        _stepsMode.Items.Add(new Choice<bool>(false, "Simple — fixed height"));
        _tabs.TabPages.Add(BuildStepsPage());
        _tabs.TabPages.Add(BuildScrollPage());
        _tabs.TabPages.Add(BuildBrowserPage());

        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 8, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_reset, 0, 0);
        buttons.Controls.Add(_save, 2, 0);
        buttons.Controls.Add(_cancel, 3, 0);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        root.Controls.Add(_tabs);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _browse.Click += (_, _) => BrowseFolder();
        _pattern.TextChanged += (_, _) => UpdatePreview();
        _format.SelectedIndexChanged += (_, _) => { _quality.Enabled = SelectedFormat == ImageFormatKind.Jpeg; UpdatePreview(); };
        _printScreen.CheckedChanged += (_, _) => ValidateHotkeys();
        _save.Click += (_, _) => SaveAndClose();
        _reset.Click += (_, _) => LoadFrom(new AppSettings().Normalize(), _startWithWindows.Checked);

        ResumeLayout(false);
        PerformLayout();

        LoadFrom(current, StartupRegistration.IsEnabled());
    }

    // ---- Pages ------------------------------------------------------------------

    private TabPage BuildGeneralPage()
    {
        var g = Grid(2);
        AddRow(g, _startWithWindows);
        AddRow(g, _startupHint);
        AddRow(g, FieldLabel("Left-click on tray icon:"), _trayClick);
        AddRow(g, _clipboard);
        AddRow(g, _notify);

        var link = new LinkLabel { Text = "Open the settings file folder", AutoSize = true, Margin = new Padding(3, 16, 3, 3) };
        link.LinkClicked += (_, _) => OpenFolder(SettingsStore.Folder);
        AddRow(g, link);

        if (Setup.Installer.CanOfferInstall)
        {
            var install = new LinkLabel { Text = "Install SnapLite on this PC (Start menu, uninstall from Settings › Apps)…", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            install.LinkClicked += (_, _) => InstallNow();
            AddRow(g, install);
        }
        return Page("General", g);
    }

    private TabPage BuildSavingPage()
    {
        var g = Grid(3);
        AddRow(g, FieldLabel("Save folder:"), _folder, _browse);
        AddRow(g, FieldLabel("File name:"), _pattern);
        AddRow(g, new Label(), _patternPreview);
        AddRow(g, new Label(), Hint("{yyyy-MM-dd_HH-mm-ss} = date and time (any .NET date format)\n{title} = window or page title"));
        AddRow(g, FieldLabel("Format:"), _format);
        AddRow(g, FieldLabel("JPEG quality:"), _quality);
        return Page("Saving", g);
    }

    private TabPage BuildShortcutsPage()
    {
        var g = Grid(3);
        AddRow(g, Hint("Click a box and press the new shortcut. Backspace clears it.\nShortcuts are paused while a box is selected."));
        foreach (var a in Actions)
            AddRow(g, FieldLabel(a.DisplayName() + ":"), _boxes[a], _status[a]);
        AddRow(g, _printScreen);
        AddRow(g, _printScreenStatus);
        return Page("Shortcuts", g);
    }

    private TabPage BuildWebPage()
    {
        var g = Grid(2);
        AddRow(g, Hint("Defaults for “Full webpage”. The capture dialog remembers what you pick there."));
        AddRow(g, FieldLabel("Viewport width:"), _webViewport);
        AddRow(g, _webBanners);
        AddRow(g, _webFloating);
        AddRow(g, _webAnimations);
        AddRow(g, FieldLabel("Extra wait before capture (ms):"), _webSettle);
        AddRow(g, FieldLabel("Maximum page height (px):"), _webMaxHeight);

        var clear = new LinkLabel { Text = "Clear web capture cache and cookies", AutoSize = true, Margin = new Padding(3, 16, 3, 3) };
        clear.LinkClicked += (_, _) => ClearWebCache();
        AddRow(g, clear);
        return Page("Web capture", g);
    }

    private TabPage BuildStepsPage()
    {
        var g = Grid(2);
        AddRow(g, Hint("“Webpage in steps” saves a page as numbered images in its own folder."));
        AddRow(g, FieldLabel("Default split:"), _stepsMode);
        AddRow(g, FieldLabel("Smart: minimum step height (px):"), _stepMin);
        AddRow(g, FieldLabel("Smart: maximum step height (px):"), _stepMax);
        AddRow(g, FieldLabel("Simple: step height (px, 0 = one screen):"), _simpleHeight);
        AddRow(g, FieldLabel("Simple: overlap between steps (px):"), _simpleOverlap);
        AddRow(g, _stepsFull);
        AddRow(g, _stepsIndex);
        return Page("Steps", g);
    }

    private TabPage BuildScrollPage()
    {
        var g = Grid(2);
        AddRow(g, Hint("“Scrolling window” scrolls the window under the mouse and stitches the screens together. " +
                       "It's best effort: animated or lazily drawn content can stop it early. Press Esc or move the mouse to stop."));
        AddRow(g, _scrollTop);
        AddRow(g, FieldLabel("Wait after each scroll (ms):"), _scrollDelay);
        AddRow(g, FieldLabel("Maximum height (px):"), _scrollMax);
        return Page("Scrolling", g);
    }

    // Browser extension (acts immediately; nothing to save)
    private readonly Label _browserStatus = new() { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(3, 6, 3, 6) };
    private readonly Button _browserSetUp = new() { AutoSize = true };
    private readonly Button _browserRemove = new() { Text = "Remove", AutoSize = true };
    private readonly TextBox _browserFolder = new() { ReadOnly = true, Width = 360, Text = Bridge.BrowserSetup.ExtensionFolder };

    private TabPage BuildBrowserPage()
    {
        var g = Grid(3);
        AddRow(g, Hint("Capture the page you're looking at in Chrome or Edge, including pages you're logged in to: " +
                       "full page, in steps or the visible part. Captures are saved here, with your folder, clipboard and " +
                       "notification settings."));
        AddRow(g, _browserStatus);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(_browserSetUp);
        buttons.Controls.Add(_browserRemove);
        AddRow(g, buttons);

        AddRow(g, Hint("Then add the extension to your browser (once):\n" +
                       "1. Open the extensions page and turn on “Developer mode”.\n" +
                       "2. Click “Load unpacked” and choose this folder:"));
        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(_browserFolder.Text); } catch { } };
        AddRow(g, _browserFolder, copy);

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        foreach (string browser in new[] { "Chrome", "Edge" })
        {
            if (!Bridge.BrowserSetup.IsInstalled(browser)) continue;
            var link = new LinkLabel { Text = $"Open {browser}'s extensions page", AutoSize = true, Margin = new Padding(3, 3, 12, 3) };
            link.LinkClicked += (_, _) => Bridge.BrowserSetup.OpenExtensionsPage(browser);
            links.Controls.Add(link);
        }
        AddRow(g, links);
        AddRow(g, Hint("3. Pin SnapLite next to the address bar. Shortcuts: Alt+Shift+F full page, Alt+Shift+S steps, " +
                       "Alt+Shift+V visible part (change them at chrome://extensions/shortcuts)."));

        _browserSetUp.Click += (_, _) => RunBrowserSetup(remove: false);
        _browserRemove.Click += (_, _) => RunBrowserSetup(remove: true);
        UpdateBrowserStatus();
        return Page("Browser", g);
    }

    private void RunBrowserSetup(bool remove)
    {
        try
        {
            if (remove) Bridge.BrowserSetup.Remove();
            else Bridge.BrowserSetup.SetUp();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"That didn't work:\n{ex.Message}", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateBrowserStatus();
    }

    private void UpdateBrowserStatus()
    {
        bool on = Bridge.BrowserSetup.IsSetUp;
        _browserStatus.Text = on
            ? "✓ Set up. SnapLite is registered for Chrome, Edge and Brave, and the extension files are in the folder below."
            : "Not set up yet.";
        _browserStatus.ForeColor = on ? Ok : SystemColors.ControlText;
        _browserSetUp.Text = on ? "Repair / update" : "Set up";
        _browserRemove.Enabled = on;
    }

    /// <summary>Installs this downloaded copy, then hands over to the installed one.</summary>
    private void InstallNow()
    {
        try
        {
            Setup.Installer.Install(_startWithWindows.Checked, stopRunning: false); // the running copy is this one
            MessageBox.Show(this, "SnapLite is installed. It will now restart from its new place.", "SnapLite",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Setup.Installer.LaunchInstalledOnExit = true; // once this copy has exited and released the single-instance lock
            Application.ExitThread();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't install:\n{ex.Message}", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearWebCache()
    {
        string dir = WebCapture.WebPageSession.UserDataFolder;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            MessageBox.Show(this, "Web capture cache cleared.", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't clear it (is a web capture running?)\n{ex.Message}", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- Load / save ------------------------------------------------------------

    private void LoadFrom(AppSettings s, bool startWithWindows)
    {
        _startWithWindows.Checked = startWithWindows;
        _startupHint.Text = startWithWindows && StartupRegistration.IsDisabledInTaskManager()
            ? "Note: SnapLite is disabled in Task Manager › Startup, so Windows won't start it. Re-enable it there."
            : "";
        Select(_trayClick, s.TrayLeftClick);
        _clipboard.Checked = s.CopyToClipboard;
        _notify.Checked = s.ShowNotifications;

        _folder.Text = s.SaveFolder;
        _pattern.Text = s.FileNamePattern;
        Select(_format, s.ImageFormat);
        _quality.Value = Math.Clamp(s.JpegQuality, 1, 100);
        _quality.Enabled = s.ImageFormat == ImageFormatKind.Jpeg;

        foreach (var a in Actions)
            _boxes[a].Binding = HotkeyBinding.TryParse(s.Hotkeys.GetValueOrDefault(a), out var b) ? b : null;
        _printScreen.Checked = s.UsePrintScreen;

        if (!AppSettings.ViewportPresets.Contains(s.WebViewportWidth) &&
            !_webViewport.Items.Cast<Choice<int>>().Any(c => c.Value == s.WebViewportWidth))
            _webViewport.Items.Add(new Choice<int>(s.WebViewportWidth, $"Custom — {s.WebViewportWidth} px"));
        Select(_webViewport, s.WebViewportWidth);
        _webBanners.Checked = s.WebHideBanners;
        _webFloating.Checked = s.WebFixFloating;
        _webAnimations.Checked = s.WebDisableAnimations;
        _webSettle.Value = Math.Clamp(s.WebSettleDelayMs, 0, 10000);
        _webMaxHeight.Value = Math.Clamp(s.WebMaxPageHeight, 2000, 100000);

        Select(_stepsMode, s.StepsSmart);
        _stepMax.Value = Math.Clamp(s.StepMaxHeight, 300, 10000);
        _stepMin.Value = Math.Clamp(s.StepMinHeight, 100, 9900);
        _simpleHeight.Value = Math.Clamp(s.SimpleStepHeight, 0, 10000);
        _simpleOverlap.Value = Math.Clamp(s.SimpleStepOverlap, 0, 500);
        _stepsFull.Checked = s.StepsSaveFullImage;
        _stepsIndex.Checked = s.StepsWriteIndex;

        _scrollTop.Checked = s.ScrollStartFromTop;
        _scrollDelay.Value = Math.Clamp(s.ScrollSettleDelayMs, 0, 3000);
        _scrollMax.Value = Math.Clamp(s.ScrollMaxHeight, 1000, 100000);

        UpdatePreview();
        ValidateHotkeys();
    }

    private AppSettings BuildSettings()
    {
        var s = new AppSettings
        {
            SaveFolder = _folder.Text.Trim(),
            FileNamePattern = _pattern.Text.Trim(),
            ImageFormat = SelectedFormat,
            JpegQuality = (int)_quality.Value,
            CopyToClipboard = _clipboard.Checked,
            ShowNotifications = _notify.Checked,
            TrayLeftClick = ((Choice<CaptureAction>)_trayClick.SelectedItem!).Value,
            UsePrintScreen = _printScreen.Checked,
            SkipInstallPrompt = _skipInstallPrompt,
            Hotkeys = Actions.ToDictionary(a => a, a => _boxes[a].Binding?.ToString() ?? ""),
            WebViewportWidth = ((Choice<int>)_webViewport.SelectedItem!).Value,
            WebHideBanners = _webBanners.Checked,
            WebFixFloating = _webFloating.Checked,
            WebDisableAnimations = _webAnimations.Checked,
            WebSettleDelayMs = (int)_webSettle.Value,
            WebMaxPageHeight = (int)_webMaxHeight.Value,
            StepsSmart = ((Choice<bool>)_stepsMode.SelectedItem!).Value,
            StepMinHeight = (int)_stepMin.Value,
            StepMaxHeight = (int)_stepMax.Value,
            SimpleStepHeight = (int)_simpleHeight.Value,
            SimpleStepOverlap = (int)_simpleOverlap.Value,
            StepsSaveFullImage = _stepsFull.Checked,
            StepsWriteIndex = _stepsIndex.Checked,
            ScrollStartFromTop = _scrollTop.Checked,
            ScrollSettleDelayMs = (int)_scrollDelay.Value,
            ScrollMaxHeight = (int)_scrollMax.Value,
        };
        return s.Normalize();
    }

    private void SaveAndClose()
    {
        if (!ValidateHotkeys())
        {
            _tabs.SelectedIndex = 2;
            return;
        }

        AppSettings s = BuildSettings();
        try
        {
            Directory.CreateDirectory(s.SaveFolder);
        }
        catch (Exception ex)
        {
            _tabs.SelectedIndex = 1;
            MessageBox.Show(this, $"Can't use this save folder:\n{ex.Message}", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        IReadOnlyList<string> problems;
        try
        {
            problems = _host.ApplySettings(s, _startWithWindows.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save settings:\n{ex.Message}", "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(this, "Settings were saved, but:\n\n" + string.Join("\n", problems),
                "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ValidateHotkeys();
            return; // stay open so it can be fixed
        }
        Close();
    }

    // ---- Validation -------------------------------------------------------------

    /// <summary>Updates the status next to every shortcut. Returns false if something must be fixed before saving.</summary>
    private bool ValidateHotkeys()
    {
        var entries = new List<(HotkeyBinding Binding, Label Status)>();
        foreach (var a in Actions)
        {
            if (_boxes[a].Binding is { } b) entries.Add((b, _status[a]));
            else SetStatus(_status[a], "Not set", SystemColors.GrayText);
        }
        var printScreen = new HotkeyBinding(Native.HotkeyModifiers.None, Keys.PrintScreen);
        if (_printScreen.Checked) entries.Add((printScreen, _printScreenStatus));
        else SetStatus(_printScreenStatus, "Windows uses Print Screen for its own snipping tool by default.", SystemColors.GrayText);

        bool ok = true;
        foreach (var (binding, status) in entries)
        {
            string? error = binding.Validate();
            if (error == null && entries.Count(e => e.Binding == binding) > 1) error = "Used twice";

            if (error != null)
            {
                SetStatus(status, "✗ " + error, Bad);
                ok = false;
            }
            else if (!_host.IsHotkeyAvailable(binding))
                SetStatus(status, "⚠ In use by another app" + HotkeyManager.PrintScreenHint(binding), Warn);
            else
                SetStatus(status, "✓ Available", Ok);
        }
        _save.Enabled = ok;
        return ok;
    }

    private static void SetStatus(Label l, string text, Color color)
    {
        l.Text = text;
        l.ForeColor = color;
    }

    // ---- Helpers ----------------------------------------------------------------

    private ImageFormatKind SelectedFormat =>
        (_format.SelectedItem as Choice<ImageFormatKind>)?.Value ?? ImageFormatKind.Png;

    private void UpdatePreview()
    {
        string ext = SelectedFormat == ImageFormatKind.Png ? ".png" : ".jpg";
        string name = FileNamer.BuildBaseName(_pattern.Text, DateTime.Now, "Window title");
        _patternPreview.Text = "Example: " + name + ext;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where SnapLite saves captures",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : AppSettings.DefaultSaveFolder,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static void Select<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items)
            if (item is Choice<T> c && EqualityComparer<T>.Default.Equals(c.Value, value)) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static TableLayoutPanel Grid(int columns)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
        };
        for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    /// <summary>Adds a row; the last cell spans whatever columns are left.</summary>
    private static void AddRow(TableLayoutPanel t, params Control[] cells)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int col = 0; col < cells.Length; col++)
        {
            t.Controls.Add(cells[col], col, row);
            if (col == cells.Length - 1 && cells.Length < t.ColumnCount)
                t.SetColumnSpan(cells[col], t.ColumnCount - col);
        }
    }

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(6), UseVisualStyleBackColor = true, AutoScroll = true };
        p.Controls.Add(content);
        return p;
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) };

    private static Label Hint(string text = "") =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(500, 0), Margin = new Padding(3, 4, 3, 6) };

    private static CheckBox Check(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    private static ComboBox Combo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    // ---- Window behaviour -------------------------------------------------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            Hide(); // minimize = back to the tray
            WindowState = FormWindowState.Normal;
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _host.ResumeHotkeys(); // never leave hotkeys suspended while the user is elsewhere
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (ActiveControl is HotkeyBox) _host.SuspendHotkeys();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _host.ResumeHotkeys();
        base.OnFormClosed(e);
    }
}
