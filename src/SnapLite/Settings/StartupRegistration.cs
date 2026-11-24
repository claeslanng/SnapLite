using Microsoft.Win32;

namespace SnapLite.Settings;

/// <summary>"Start with Windows" via HKCU\...\Run (per user, no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "SnapLite";

    public const string AutostartArg = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {AutostartArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If the exe was moved since autostart was enabled, point the Run entry at the new path.</summary>
    public static void SyncPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current &&
                !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch { /* not critical */ }
    }

    /// <summary>
    /// True when the user turned SnapLite off in Task Manager → Startup. Windows then ignores
    /// the Run entry, so the settings window explains it instead of silently doing nothing.
    /// </summary>
    public static bool IsDisabledInTaskManager()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte: 0x02 / 0x06 = enabled, 0x03 / 0x07 = disabled.
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 0x01) != 0;
        }
        catch { return false; }
    }
}
