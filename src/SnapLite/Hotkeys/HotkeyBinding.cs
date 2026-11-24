using SnapLite.Interop;

namespace SnapLite.Hotkeys;

/// <summary>A key plus modifiers, parsed from / formatted to text like "Ctrl+Shift+1".</summary>
internal readonly record struct HotkeyBinding(Native.HotkeyModifiers Modifiers, Keys Key)
{
    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = Native.HotkeyModifiers.None;
        Keys key = Keys.None;

        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.HotkeyModifiers.Control; break;
                case "shift": mods |= Native.HotkeyModifiers.Shift; break;
                case "alt": mods |= Native.HotkeyModifiers.Alt; break;
                case "win" or "windows": mods |= Native.HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None) return false; // two non-modifier keys
                    key = ParseKey(part);
                    if (key == Keys.None) return false;
                    break;
            }
        }

        if (key == Keys.None) return false;
        binding = new HotkeyBinding(mods, key);
        return true;
    }

    private static Keys ParseKey(string p)
    {
        if (p.Length == 1 && char.IsAsciiDigit(p[0])) return Keys.D0 + (p[0] - '0');
        if (p.Length == 1 && char.IsAsciiLetter(p[0])) return (Keys)char.ToUpperInvariant(p[0]);
        switch (p.ToLowerInvariant())
        {
            case "prtsc" or "printscreen" or "print": return Keys.PrintScreen;
        }
        if (Enum.TryParse(p, ignoreCase: true, out Keys k) && !char.IsAsciiDigit(p[0]) &&
            k is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin))
            return k;
        return Keys.None;
    }

    /// <summary>
    /// Returns why this shortcut would be a bad idea, or null if it's fine.
    /// Letters, digits etc. need Ctrl, Alt or Win so normal typing isn't hijacked.
    /// </summary>
    public string? Validate()
    {
        bool standalone = Key is (>= Keys.F1 and <= Keys.F24) or Keys.PrintScreen or Keys.Pause or Keys.Scroll;
        const Native.HotkeyModifiers strong = Native.HotkeyModifiers.Control | Native.HotkeyModifiers.Alt | Native.HotkeyModifiers.Win;
        if (!standalone && (Modifiers & strong) == 0)
            return "Add Ctrl, Alt or Win";
        return null;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(Native.HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Native.HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(Native.HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(Native.HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (Key - Keys.D0))).ToString(),
            Keys.PrintScreen => "PrintScreen",
            _ => Key.ToString(),
        });
        return string.Join("+", parts);
    }
}
