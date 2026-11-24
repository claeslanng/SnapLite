using SnapLite.Interop;

namespace SnapLite.Hotkeys;

/// <summary>
/// Read-only text box that records a key combination when you press it.
/// Backspace/Delete clears it; Tab, Esc and Enter keep their normal dialog meaning.
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private HotkeyBinding? _binding;

    public event EventHandler? BindingChanged;

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false; // no Ctrl+C/V/A handling, no context menu
        Text = "None";
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding? Binding
    {
        get => _binding;
        set
        {
            _binding = value;
            Text = value?.ToString() ?? "None";
        }
    }

    private void SetBinding(HotkeyBinding? value)
    {
        if (_binding == value) { Binding = value; return; }
        Binding = value;
        BindingChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        bool win = Native.IsWinKeyDown();

        // Keep normal dialog navigation.
        if (key == Keys.Tab && (mods & ~Keys.Shift) == 0 && !win) return base.ProcessCmdKey(ref msg, keyData);
        if (mods == Keys.None && !win && key is Keys.Escape or Keys.Enter) return base.ProcessCmdKey(ref msg, keyData);

        if (mods == Keys.None && !win && key is Keys.Back or Keys.Delete)
        {
            SetBinding(null);
            return true;
        }

        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            Text = Describe(mods, win) + "…"; // show what's held so far
            return true;
        }

        SetBinding(new HotkeyBinding(ToNative(mods, win), key));
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Print Screen only produces a key-up message.
        if (e.KeyCode == Keys.PrintScreen)
        {
            SetBinding(new HotkeyBinding(ToNative(e.Modifiers, Native.IsWinKeyDown()), Keys.PrintScreen));
            e.Handled = true;
            return;
        }
        // Released modifiers without completing a combination: show the saved value again.
        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin && e.Modifiers == Keys.None)
            Binding = _binding;
        base.OnKeyUp(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        Binding = _binding; // clear any "Ctrl+…" partial text
        base.OnLeave(e);
    }

    private static Native.HotkeyModifiers ToNative(Keys mods, bool win)
    {
        var m = Native.HotkeyModifiers.None;
        if ((mods & Keys.Control) != 0) m |= Native.HotkeyModifiers.Control;
        if ((mods & Keys.Shift) != 0) m |= Native.HotkeyModifiers.Shift;
        if ((mods & Keys.Alt) != 0) m |= Native.HotkeyModifiers.Alt;
        if (win) m |= Native.HotkeyModifiers.Win;
        return m;
    }

    private static string Describe(Keys mods, bool win)
    {
        var parts = new List<string>(4);
        if ((mods & Keys.Control) != 0) parts.Add("Ctrl");
        if ((mods & Keys.Shift) != 0) parts.Add("Shift");
        if ((mods & Keys.Alt) != 0) parts.Add("Alt");
        if (win) parts.Add("Win");
        return string.Join("+", parts) + (parts.Count > 0 ? "+" : "");
    }
}
