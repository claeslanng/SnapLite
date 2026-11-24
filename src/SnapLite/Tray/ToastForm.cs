using System.Drawing.Drawing2D;
using Microsoft.Win32;
using SnapLite.Interop;

namespace SnapLite.Tray;

/// <summary>A toast button. <c>Run</c> returns a new label to show (e.g. "Copied") or null to close the toast.</summary>
internal sealed record ToastAction(string Label, Func<string?> Run);

/// <summary>
/// "Saved" notification in the bottom-right corner: thumbnail, a few lines of text and buttons
/// (Open, Show in folder, Copy…). Owner-drawn (no child controls), never takes focus, is left out
/// of SnapLite's own screenshots, follows the Windows light/dark setting and closes itself after a
/// few seconds (paused while the mouse is over it). Its timer only exists while it's on screen.
/// </summary>
internal sealed class ToastForm : Form
{
    private const int LifetimeMs = 7000, TickMs = 200;

    private readonly string _title;
    private readonly string[] _lines;
    private readonly Bitmap? _thumb;
    private readonly Action? _onBodyClick;
    private readonly ToastAction[] _actions;
    private readonly string[] _labels;
    private readonly Rectangle[] _buttonRects;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = TickMs };
    private readonly Font _titleFont, _textFont;
    private readonly float _s;
    private readonly Palette _c;

    private Rectangle _thumbRect, _closeRect;
    private int _textX, _lineH, _titleH;
    private int _remainingMs = LifetimeMs;
    private int _hover = -1; // button index; Close = -2; thumbnail = -3
    private bool _mouseInside;

    private const int HoverClose = -2, HoverThumb = -3;

    public ToastForm(string title, IEnumerable<string?> lines, Image? source, IReadOnlyList<ToastAction> actions, Action? onBodyClick,
        bool? dark = null)
    {
        _title = title;
        _lines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).Take(3).ToArray();
        _actions = actions.ToArray();
        _labels = _actions.Select(a => a.Label).ToArray();
        _buttonRects = new Rectangle[_actions.Length];
        _onBodyClick = onBodyClick;
        _c = dark ?? IsDarkTheme() ? Palette.Dark : Palette.Light;

        // Size everything for the monitor the toast appears on (the one with the cursor).
        var screen = Screen.FromPoint(Cursor.Position);
        _s = Native.DpiAt(new Point(screen.WorkingArea.Right - 1, screen.WorkingArea.Bottom - 1)) / 96f;
        _titleFont = new Font("Segoe UI Semibold", 14f * _s, GraphicsUnit.Pixel);
        _textFont = new Font("Segoe UI", 12.5f * _s, GraphicsUnit.Pixel);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = _c.Back;
        DoubleBuffered = true;
        Text = "SnapLite";

        if (source != null) _thumb = MakeThumbnail(source, new Size(S(96), S(72)));
        var size = ArrangeParts();
        var wa = screen.WorkingArea;
        Bounds = new Rectangle(wa.Right - size.Width - S(12), wa.Bottom - size.Height - S(12), size.Width, size.Height);

        _timer.Tick += (_, _) =>
        {
            if (_mouseInside) return;
            _remainingMs -= TickMs;
            if (_remainingMs <= 0) Close();
        };
    }

    private int S(float v) => (int)Math.Round(v * _s);

    // ---- Layout & painting -----------------------------------------------------

    private Size ArrangeParts()
    {
        int pad = S(14), width = S(380);
        _titleH = TextRenderer.MeasureText("Ag", _titleFont).Height;
        _lineH = TextRenderer.MeasureText("Ag", _textFont).Height;
        _closeRect = new Rectangle(width - S(8) - S(26), S(8), S(26), S(26));

        _thumbRect = _thumb == null ? Rectangle.Empty : new Rectangle(pad, pad, _thumb.Width, _thumb.Height);
        _textX = _thumb == null ? pad : _thumbRect.Right + S(12);
        int textBottom = pad + _titleH + S(2) + _lines.Length * _lineH;
        int y = Math.Max(textBottom, _thumbRect.Bottom) + S(12);

        int x = _textX, h = S(30);
        for (int i = 0; i < _labels.Length; i++)
        {
            int w = TextRenderer.MeasureText(_labels[i], _textFont).Width + S(22);
            _buttonRects[i] = new Rectangle(x, y, w, h);
            x += w + S(8);
        }
        int bottom = _labels.Length > 0 ? y + h : y - S(12);
        return new Size(width, bottom + pad);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var border = new Pen(_c.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        if (_thumb != null)
        {
            g.DrawImageUnscaled(_thumb, _thumbRect.Location);
            using var pen = new Pen(_hover == HoverThumb ? _c.Accent : _c.Border);
            g.DrawRectangle(pen, _thumbRect.X - 1, _thumbRect.Y - 1, _thumbRect.Width + 1, _thumbRect.Height + 1);
        }

        const TextFormatFlags oneLine = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int textW = _closeRect.Left - S(4) - _textX;
        int y = S(14);
        TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(_textX, y, textW, _titleH), _c.Text, oneLine);
        y += _titleH + S(2);
        foreach (var line in _lines)
        {
            TextRenderer.DrawText(g, line, _textFont, new Rectangle(_textX, y, Width - S(14) - _textX, _lineH), _c.SubText,
                oneLine | TextFormatFlags.PathEllipsis);
            y += _lineH;
        }

        // Close "×"
        if (_hover == HoverClose) using (var b = new SolidBrush(_c.ButtonHover)) g.FillRectangle(b, _closeRect);
        using (var pen = new Pen(_c.SubText, Math.Max(1f, _s)))
        {
            int m = S(9);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(pen, _closeRect.Left + m, _closeRect.Top + m, _closeRect.Right - m, _closeRect.Bottom - m);
            g.DrawLine(pen, _closeRect.Right - m, _closeRect.Top + m, _closeRect.Left + m, _closeRect.Bottom - m);
            g.SmoothingMode = SmoothingMode.None;
        }

        for (int i = 0; i < _labels.Length; i++)
        {
            bool primary = i == 0, hot = _hover == i;
            Color back = primary ? (hot ? _c.AccentHover : _c.Accent) : (hot ? _c.ButtonHover : _c.Button);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, _buttonRects[i]);
            TextRenderer.DrawText(g, _labels[i], _textFont, _buttonRects[i], primary ? _c.AccentText : _c.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // ---- Mouse -------------------------------------------------------------------

    private int HitTest(Point p)
    {
        if (_closeRect.Contains(p)) return HoverClose;
        for (int i = 0; i < _buttonRects.Length; i++) if (_buttonRects[i].Contains(p)) return i;
        if (_thumb != null && _thumbRect.Contains(p)) return HoverThumb;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouseInside = true;
        int hit = HitTest(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit == -1 && _onBodyClick == null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _remainingMs = Math.Max(_remainingMs, 2500); // a moment to come back
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;

        int hit = HitTest(e.Location);
        if (hit == HoverClose) { Close(); return; }
        if (hit >= 0)
        {
            string? newLabel = Safe(() => _actions[hit].Run());
            if (newLabel == null) { Close(); return; }
            _labels[hit] = newLabel;
            ArrangeParts();
            Invalidate();
            return;
        }
        if (_onBodyClick != null) { Safe(() => { _onBodyClick(); return null; }); Close(); }
    }

    private static string? Safe(Func<string?> f)
    {
        try { return f(); }
        catch (Exception ex) { AppLog.Error("toast action", ex); return null; }
    }

    // ---- Window --------------------------------------------------------------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); // never in the next screenshot
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return; // sized for its monitor already; don't let Windows rescale it
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _thumb?.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Helpers -------------------------------------------------------------------

    /// <summary>
    /// Developer aid: <c>SnapLite.exe --toast-preview out.png [image] [dark|light]</c> draws the toast
    /// into a PNG without showing anything on screen.
    /// </summary>
    internal static int RenderPreview(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "toast-preview.png";
        using Image? sample = args.Length > 2 && File.Exists(args[2]) ? Image.FromFile(args[2]) : null;
        bool? dark = args.Contains("dark") ? true : args.Contains("light") ? false : null;
        ToastAction[] actions = [new("Open", () => null), new("Show in folder", () => null), new("Copy", () => "Copied ✓")];
        using var toast = new ToastForm($"Saved {sample?.Width ?? 1852} × {sample?.Height ?? 2977}",
            ["SnapLite_2026-11-23_23-49-46.png", "Copied to clipboard"], sample, actions, () => { }, dark);
        using var bmp = new Bitmap(toast.Width, toast.Height);
        toast.CreateControl();
        toast.DrawToBitmap(bmp, new Rectangle(Point.Empty, toast.Size));
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }

    /// <summary>Scaled copy that fills <paramref name="box"/>; tall images show their top part.</summary>
    internal static Bitmap MakeThumbnail(Image source, Size box)
    {
        double scale = Math.Max(box.Width / (double)source.Width, box.Height / (double)source.Height);
        scale = Math.Min(scale, 1.0); // a tiny capture stays its own size
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var thumb = new Bitmap(Math.Min(w, box.Width), Math.Min(h, box.Height));
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int x = (thumb.Width - w) / 2; // centred horizontally, top-aligned
        g.DrawImage(source, new Rectangle(x, 0, w, h));
        return thumb;
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private sealed record Palette(Color Back, Color Border, Color Text, Color SubText, Color Button, Color ButtonHover,
        Color Accent, Color AccentHover, Color AccentText)
    {
        public static readonly Palette Light = new(Color.FromArgb(249, 249, 249), Color.FromArgb(208, 208, 208),
            Color.FromArgb(27, 27, 27), Color.FromArgb(96, 96, 96), Color.FromArgb(232, 232, 232), Color.FromArgb(218, 218, 218),
            Color.FromArgb(0, 95, 184), Color.FromArgb(25, 117, 197), Color.White);

        public static readonly Palette Dark = new(Color.FromArgb(43, 43, 43), Color.FromArgb(70, 70, 70),
            Color.FromArgb(242, 242, 242), Color.FromArgb(178, 178, 178), Color.FromArgb(58, 58, 58), Color.FromArgb(74, 74, 74),
            Color.FromArgb(76, 194, 255), Color.FromArgb(110, 207, 255), Color.Black);
    }
}
