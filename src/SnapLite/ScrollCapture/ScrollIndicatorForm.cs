using SnapLite.Interop;

namespace SnapLite.ScrollCapture;

/// <summary>
/// Small "Scroll capture… Esc to stop" banner. It is excluded from screen capture
/// (WDA_EXCLUDEFROMCAPTURE), never takes focus, and owns a temporary Esc hotkey.
/// </summary>
internal sealed class ScrollIndicatorForm : Form
{
    private const int EscHotkeyId = 0xBFF0;
    private readonly Label _label = new()
    {
        AutoSize = true,
        ForeColor = Color.White,
        Padding = new Padding(12, 8, 12, 8),
    };
    private readonly CancellationTokenSource _cts = new();
    private bool _escRegistered;

    public CancellationToken Token => _cts.Token;

    public ScrollIndicatorForm(Rectangle target, Point avoid)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(_label);
        SetText(0);

        // Top centre of the target's monitor, unless that would sit under the scroll point.
        var screen = Screen.FromRectangle(target).WorkingArea;
        var size = GetPreferredSize(Size.Empty);
        var loc = new Point(screen.Left + (screen.Width - size.Width) / 2, screen.Top + 12);
        if (new Rectangle(loc, size).Contains(avoid)) loc.Y = screen.Bottom - size.Height - 12;
        Location = loc;
    }

    public void SetText(int pixels) =>
        _label.Text = $"Scroll capture (best effort) · {pixels:N0} px · Esc or move the mouse to stop";

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); // not in our own screenshots
        _escRegistered = Native.RegisterHotKey(Handle, EscHotkeyId, Native.HotkeyModifiers.NoRepeat, Native.VK_ESCAPE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == EscHotkeyId) _cts.Cancel();
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (_escRegistered && IsHandleCreated) Native.UnregisterHotKey(Handle, EscHotkeyId);
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
