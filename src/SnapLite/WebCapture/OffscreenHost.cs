using SnapLite.Interop;

namespace SnapLite.WebCapture;

/// <summary>
/// A borderless window parked outside the visible desktop that hosts the WebView2.
/// It never takes focus or shows in the taskbar / Alt+Tab.
/// </summary>
internal sealed class OffscreenHost : Form
{
    public OffscreenHost(Size size)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Rectangle vs = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(vs.Left - size.Width - 200, vs.Top, size.Width, size.Height);
        Text = "SnapLite web capture";
    }

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
}
