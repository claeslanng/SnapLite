namespace SnapLite.Interop;

/// <summary>
/// Invisible message-only window (HWND_MESSAGE). Receives WM_HOTKEY and the
/// "show yourself" message from a second instance. Costs nothing while idle:
/// it only wakes when Windows posts a message to it.
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    public const string WindowName = "SnapLite.MessageWindow.5b1f7c2e";

    /// <summary>Raised with (message, wParam) for WM_HOTKEY and WM_SNAPLITE_SHOW.</summary>
    public event Action<int, IntPtr>? MessageReceived;

    public MessageWindow()
    {
        CreateHandle(new CreateParams { Caption = WindowName, Parent = Native.HWND_MESSAGE });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is Native.WM_HOTKEY or Native.WM_SNAPLITE_SHOW)
        {
            MessageReceived?.Invoke(m.Msg, m.WParam);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
