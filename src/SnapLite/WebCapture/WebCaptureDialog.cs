using SnapLite.Interop;
using SnapLite.Settings;

namespace SnapLite.WebCapture;

/// <summary>
/// "Capture full webpage" dialog: URL (pre-filled from the clipboard), viewport width and
/// clean-up toggles. The capture runs inside the dialog with progress and Cancel.
/// </summary>
internal sealed class WebCaptureDialog : Form
{
    private sealed record Choice(int Width, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly TextBox _url = new() { Width = 380, PlaceholderText = "https://example.com" };
    private readonly ComboBox _viewport = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _banners = Check("Hide cookie banners and pop-ups");
    private readonly CheckBox _floating = Check("Fix sticky headers and floating widgets");
    private readonly CheckBox _animations = Check("Disable animations");
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Width = 440, Height = 6, Visible = false };
    private readonly Button _capture = new() { Text = "Capture", AutoSize = true, MinimumSize = new Size(90, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0) };

    private readonly ComboBox _split = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    private readonly AppSettings _settings;
    private readonly bool _stepsMode;
    private CancellationTokenSource? _cts;
    private bool _running, _closeWhenStopped;

    public WebPageCapturer.Result? Result { get; private set; }
    public WebPageCapturer.StepsResult? StepsResult { get; private set; }
    public WebCaptureOptions? UsedOptions { get; private set; }
    public bool UsedSmartSplit { get; private set; }

    public WebCaptureDialog(AppSettings settings, Icon icon, bool stepsMode = false)
    {
        _settings = settings;
        _stepsMode = stepsMode;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = stepsMode ? "Capture webpage in steps" : "Capture full webpage";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = _capture;
        Padding = new Padding(12);

        foreach (int w in AppSettings.ViewportPresets)
            _viewport.Items.Add(new Choice(w, Describe(w)));
        if (!AppSettings.ViewportPresets.Contains(settings.WebViewportWidth))
            _viewport.Items.Add(new Choice(settings.WebViewportWidth, $"Custom — {settings.WebViewportWidth} px"));
        _viewport.SelectedItem = _viewport.Items.Cast<Choice>().First(c => c.Width == settings.WebViewportWidth);

        _banners.Checked = settings.WebHideBanners;
        _floating.Checked = settings.WebFixFloating;
        _animations.Checked = settings.WebDisableAnimations;
        _url.Text = UrlFromClipboard() ?? "";

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, Label("Page address:"), _url);
        AddRow(grid, Label("Viewport width:"), _viewport);
        if (stepsMode)
        {
            _split.Items.Add($"Smart — by sections ({settings.StepMinHeight}–{settings.StepMaxHeight} px)");
            _split.Items.Add(settings.SimpleStepHeight > 0
                ? $"Simple — every {settings.SimpleStepHeight} px"
                : "Simple — one screen per step");
            _split.SelectedIndex = settings.StepsSmart ? 0 : 1;
            AddRow(grid, Label("Split:"), _split);
        }
        AddRow(grid, new Label { AutoSize = true }, _banners);
        AddRow(grid, new Label { AutoSize = true }, _floating);
        AddRow(grid, new Label { AutoSize = true }, _animations);
        AddRow(grid, _status);
        AddRow(grid, _progress);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_capture);
        AddRow(grid, buttons);
        Controls.Add(grid);

        _status.Text = "The page loads in a hidden browser window. Pages that need you to be logged in may show a login screen instead.";
        _capture.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { if (_running) _cts?.Cancel(); else Close(); };

        ResumeLayout(false);
        PerformLayout();
    }

    private static string Describe(int w) => w switch
    {
        1280 => "Desktop — 1280 px",
        1440 => "Laptop — 1440 px",
        1920 => "Full HD — 1920 px",
        390 => "Mobile — 390 px",
        _ => $"{w} px",
    };

    private static string? UrlFromClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? WebPageCapturer.NormalizeUrl(Clipboard.GetText()) : null;
        }
        catch { return null; } // clipboard busy
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Native.SetForegroundWindow(Handle);
        _url.Focus();
        _url.SelectAll();
    }

    private async Task RunAsync()
    {
        if (_running) return;
        string? url = WebPageCapturer.NormalizeUrl(_url.Text);
        if (url == null)
        {
            ShowStatus("Enter a web address, for example https://example.com", error: true);
            _url.Focus();
            return;
        }

        var options = new WebCaptureOptions(
            url,
            ((Choice)_viewport.SelectedItem!).Width,
            _banners.Checked,
            _floating.Checked,
            _animations.Checked,
            _settings.WebSettleDelayMs,
            _settings.WebMaxPageHeight);

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(s => ShowStatus(s, error: false));
        try
        {
            if (_stepsMode)
            {
                bool smart = _split.SelectedIndex == 0;
                var steps = _settings.ToStepSettings() with { Smart = smart };
                StepsResult = await WebPageCapturer.CaptureStepsAsync(options, steps, progress, _cts.Token);
                UsedSmartSplit = smart;
            }
            else
            {
                Result = await WebPageCapturer.CaptureAsync(options, progress, _cts.Token);
            }
            UsedOptions = options;
            DialogResult = DialogResult.OK; // closes the dialog
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Cancelled.", error: false);
        }
        catch (WebCaptureException ex)
        {
            ShowStatus(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            ShowStatus("Capture failed: " + ex.Message, error: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
            if (_closeWhenStopped && DialogResult != DialogResult.OK) Close();
        }
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _progress.Visible = running;
        _capture.Enabled = !running;
        _url.Enabled = _viewport.Enabled = _split.Enabled = _banners.Enabled = _floating.Enabled = _animations.Enabled = !running;
        UseWaitCursor = running;
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.FromArgb(196, 43, 28) : SystemColors.GrayText;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && DialogResult != DialogResult.OK)
        {
            // Stop the capture first; the dialog closes once the WebView2 has been disposed.
            e.Cancel = true;
            _closeWhenStopped = true;
            _cts?.Cancel();
        }
        base.OnFormClosing(e);
    }

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

    private static Label Label(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) };

    private static CheckBox Check(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
}
