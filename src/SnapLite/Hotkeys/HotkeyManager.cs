using SnapLite.Capture;
using SnapLite.Interop;

namespace SnapLite.Hotkeys;

/// <summary>Registers global hotkeys with RegisterHotKey and raises <see cref="Pressed"/>.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int ProbeId = 0xBFFE; // temporary id used only to test availability

    private readonly MessageWindow _window;
    private readonly Dictionary<int, (CaptureAction Action, HotkeyBinding Binding)> _registered = new();
    private int _nextId = 1;

    public event Action<CaptureAction>? Pressed;

    public HotkeyManager(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>
    /// Registers every binding. Returns a human-readable line for each one that failed
    /// (bad syntax, or already taken by another app), so the caller can tell the user.
    /// </summary>
    public IReadOnlyList<string> RegisterAll(IEnumerable<(CaptureAction Action, string Text)> bindings)
    {
        UnregisterAll();
        var failures = new List<string>();

        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue; // unbound on purpose
            if (!HotkeyBinding.TryParse(text, out var binding))
            {
                failures.Add($"{action.DisplayName()}: \"{text}\" is not a valid shortcut");
                continue;
            }

            int id = _nextId++;
            if (Native.RegisterHotKey(_window.Handle, id, binding.Modifiers | Native.HotkeyModifiers.NoRepeat, (uint)binding.Key))
                _registered[id] = (action, binding);
            else
                failures.Add($"{action.DisplayName()}: {binding} is already used by another app{PrintScreenHint(binding)}");
        }
        return failures;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
            Native.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    /// <summary>
    /// True if SnapLite already holds this combination, or if nobody else does
    /// (checked by briefly registering and releasing it).
    /// </summary>
    public bool IsAvailable(HotkeyBinding binding)
    {
        foreach (var (_, b) in _registered.Values)
            if (b == binding) return true;

        if (!Native.RegisterHotKey(_window.Handle, ProbeId, binding.Modifiers | Native.HotkeyModifiers.NoRepeat, (uint)binding.Key))
            return false;
        Native.UnregisterHotKey(_window.Handle, ProbeId);
        return true;
    }

    public static string PrintScreenHint(HotkeyBinding b) =>
        b.Key == Keys.PrintScreen && b.Modifiers == Native.HotkeyModifiers.None
            ? " (Windows may be using it for screen snipping: turn that off in Windows Settings › Ease of Access/Accessibility › Keyboard)"
            : "";

    private void OnMessage(int msg, IntPtr wParam)
    {
        if (msg == Native.WM_HOTKEY && _registered.TryGetValue((int)wParam, out var entry))
            Pressed?.Invoke(entry.Action);
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.MessageReceived -= OnMessage;
    }
}
