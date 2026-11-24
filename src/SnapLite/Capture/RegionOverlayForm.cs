using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SnapLite.Interop;

namespace SnapLite.Capture;

/// <summary>
/// Full-virtual-screen overlay showing a frozen, dimmed screenshot. Drag to select;
/// the selected area is shown undimmed with live W × H. Arrow keys move the cursor
/// 1 px (Shift: 10 px) for precise edges, Esc / right-click cancels.
/// The result is cropped from the frozen image, so you get exactly what you saw.
/// </summary>
internal sealed class RegionOverlayForm : Form
{
    private readonly Bitmap _frozen;
    private readonly Bitmap _dimmed;
    private readonly Rectangle _virtual;
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Pen _border = new(Color.FromArgb(0, 150, 255), 1f);
    private readonly SolidBrush _labelBack = new(Color.FromArgb(220, 20, 20, 20));
    private readonly Rectangle _hintRect;
    private const string Hint = "Drag to select  ·  Arrow keys nudge (Shift = 10 px)  ·  Esc to cancel";

    private Point _start;
    private bool _dragging;
    private Rectangle _sel;
    private Rectangle _dirty; // last painted decoration area

    public Rectangle Selection { get; private set; }

    /// <summary>Shows the overlay and returns the selected image, or null if cancelled.</summary>
    public static Bitmap? SelectRegion()
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        using Bitmap frozen = ScreenCapture.Capture(vs);
        using var form = new RegionOverlayForm(frozen, vs);
        if (form.ShowDialog() != DialogResult.OK || form.Selection.IsEmpty) return null;

        Bitmap result = frozen.Clone(form.Selection, frozen.PixelFormat);
        result.SetResolution(96, 96);
        return result;
    }

    private RegionOverlayForm(Bitmap frozen, Rectangle virtualScreen)
    {
        _frozen = frozen;
        _virtual = virtualScreen;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "SnapLite region";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        // Pre-render the dimmed background once; painting then only copies pixels.
        _dimmed = new Bitmap(frozen.Width, frozen.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_dimmed))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(frozen, new Rectangle(Point.Empty, frozen.Size), 0, 0, frozen.Width, frozen.Height, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            using var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            g.FillRectangle(shade, 0, 0, frozen.Width, frozen.Height);
        }

        // Hint at the top of the monitor the cursor is on (client coordinates).
        var mon = Screen.FromPoint(Cursor.Position).Bounds;
        mon.Offset(-_virtual.X, -_virtual.Y);
        Size hs = TextRenderer.MeasureText(Hint, _font);
        _hintRect = new Rectangle(mon.X + (mon.Width - hs.Width) / 2 - 10, mon.Y + 24, hs.Width + 20, hs.Height + 10);
        _dirty = _hintRect;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual; // re-assert in case anything rescaled us during creation
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // We span monitors with different DPIs on purpose; never let WinForms resize us.
        if (m.Msg is Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        if (m.Msg is Native.WM_GETDPISCALEDSIZE) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    // ---- Painting ---------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e) { /* everything is in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle clip = e.ClipRectangle;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            Rectangle bright = Rectangle.Intersect(_sel, clip);
            if (!bright.IsEmpty) g.DrawImage(_frozen, bright, bright, GraphicsUnit.Pixel);

            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.DrawRectangle(_border, _sel.X - 1, _sel.Y - 1, _sel.Width + 1, _sel.Height + 1);

            string label = $"{_sel.Width} × {_sel.Height}";
            Rectangle lr = LabelRect(label);
            g.FillRectangle(_labelBack, lr);
            TextRenderer.DrawText(g, label, _font, lr, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.FillRectangle(_labelBack, _hintRect);
            TextRenderer.DrawText(g, Hint, _font, _hintRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private Rectangle LabelRect(string label)
    {
        Size ts = TextRenderer.MeasureText(label, _font);
        int w = ts.Width + 12, h = ts.Height + 6;
        int x = _sel.X;
        int y = _sel.Bottom + 6;
        if (y + h > ClientSize.Height) y = _sel.Y - h - 6;     // no room below: put it above
        if (y < 0) y = _sel.Y + 6;                              // no room above either: inside
        x = Math.Clamp(x, 0, Math.Max(0, ClientSize.Width - w));
        return new Rectangle(x, y, w, h);
    }

    private Rectangle DecorationBounds()
    {
        if (_sel.Width <= 0 || _sel.Height <= 0) return _hintRect;
        var r = Rectangle.Inflate(_sel, 3, 3);
        return Rectangle.Union(r, Rectangle.Inflate(LabelRect($"{_sel.Width} × {_sel.Height}"), 2, 2));
    }

    private void SetSelection(Rectangle sel)
    {
        _sel = sel;
        Rectangle now = DecorationBounds();
        Invalidate(_dirty.IsEmpty ? now : Rectangle.Union(_dirty, now));
        _dirty = now;
    }

    // ---- Input ------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _start = e.Location;
            _dragging = true;
            SetSelection(new Rectangle(e.Location, new Size(1, 1)));
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (_dragging || !_sel.IsEmpty) { _dragging = false; SetSelection(Rectangle.Empty); }
            else DialogResult = DialogResult.Cancel;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        int l = Math.Min(_start.X, e.X), t = Math.Min(_start.Y, e.Y);
        int r = Math.Max(_start.X, e.X) + 1, b = Math.Max(_start.Y, e.Y) + 1; // inclusive end pixel
        var sel = Rectangle.FromLTRB(l, t, r, b);
        sel.Intersect(new Rectangle(Point.Empty, _frozen.Size));
        SetSelection(sel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        if (_sel.Width >= 2 && _sel.Height >= 2)
        {
            Selection = _sel;
            DialogResult = DialogResult.OK;
        }
        else SetSelection(Rectangle.Empty);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
        switch (key)
        {
            case Keys.Escape: DialogResult = DialogResult.Cancel; return true;
            case Keys.Left: Nudge(-step, 0); return true;
            case Keys.Right: Nudge(step, 0); return true;
            case Keys.Up: Nudge(0, -step); return true;
            case Keys.Down: Nudge(0, step); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static void Nudge(int dx, int dy)
    {
        var p = Cursor.Position;
        Cursor.Position = new Point(p.X + dx, p.Y + dy); // triggers OnMouseMove while dragging
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmed.Dispose();
            _font.Dispose();
            _border.Dispose();
            _labelBack.Dispose();
        }
        base.Dispose(disposing);
    }
}
