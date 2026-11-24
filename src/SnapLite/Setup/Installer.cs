using System.Diagnostics;
using Microsoft.Win32;
using SnapLite.Bridge;
using SnapLite.Interop;
using SnapLite.Settings;
using SnapLite.Tray;

namespace SnapLite.Setup;

/// <summary>
/// Per-user install without an installer: the downloaded exe copies itself to
/// %LOCALAPPDATA%\Programs\SnapLite, adds a Start menu shortcut and an entry in Settings › Apps
/// (with a working Uninstall). No admin rights, nothing outside the user's own profile.
///
/// On every start of a downloaded (not installed) copy:
///   not installed yet            → ask: Install / Just run it (remembered)
///   installed, this one is newer → ask: Update
///   installed, same or older     → just start the installed copy
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SnapLite";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SnapLite");
    public static string InstalledExe => Path.Combine(InstallDir, "SnapLite.exe");
    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SnapLite.lnk");

    private static string? CurrentExe => Environment.ProcessPath;

    /// <summary>Only a published single-file exe can be installed (a dev build needs the files next to it).</summary>
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);

    public static bool IsRunningInstalledCopy =>
        string.Equals(Path.GetFullPath(CurrentExe ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>True when Settings should offer "Install SnapLite on this PC".</summary>
    public static bool CanOfferInstall => IsSingleFile && !IsRunningInstalledCopy && !IsInstalled;

    public static Version CurrentVersion => typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0);

    private static Version? InstalledVersion
    {
        get
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(InstalledExe);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { return null; }
        }
    }

    public enum StartupDecision { Continue, Exit }

    /// <summary>Called before the single-instance check. Exit = another copy takes over from here.</summary>
    public static StartupDecision OnStartup(string[] args)
    {
        if (!IsSingleFile || IsRunningInstalledCopy || args.Contains(StartupRegistration.AutostartArg))
            return StartupDecision.Continue;
        try
        {
            if (IsInstalled)
            {
                var installed = InstalledVersion;
                var current = new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build);
                if (installed != null && current > installed)
                {
                    if (!AskUpdate(installed, current)) return StartupDecision.Continue; // run this copy as it is
                    Install(startWithWindows: StartupRegistration.IsEnabled());
                }
                LaunchInstalled();
                return StartupDecision.Exit;
            }

            var settings = SettingsStore.Load(out _, out _);
            if (settings.SkipInstallPrompt) return StartupDecision.Continue;

            var (install, startWithWindows) = AskInstall();
            if (install)
            {
                Install(startWithWindows);
                LaunchInstalled();
                return StartupDecision.Exit;
            }
            settings.SkipInstallPrompt = true;
            try { SettingsStore.Save(settings); } catch { }
            TrayContext.ShowWelcome = true; // saving the choice created the settings file; still say hello
        }
        catch (Exception ex)
        {
            AppLog.Error("install", ex);
            MessageBox.Show($"SnapLite couldn't be installed:\n{ex.Message}\n\nIt will run from here instead.",
                "SnapLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return StartupDecision.Continue;
    }

    // ---- Dialogs (native Windows task dialogs) --------------------------------------

    private static (bool Install, bool StartWithWindows) AskInstall()
    {
        var install = new TaskDialogCommandLinkButton("Install",
            "Adds SnapLite to the Start menu and to Settings › Apps. No admin rights needed.");
        var justRun = new TaskDialogCommandLinkButton("Just run it",
            "Run from where it is now. You can install it later in Settings.");
        var startup = new TaskDialogVerificationCheckBox("Start SnapLite when Windows starts", isChecked: true);
        using var icon = AppIcons.Create(paused: false, pixelSize: 32);
        var page = new TaskDialogPage
        {
            Caption = "SnapLite",
            Heading = "Install SnapLite?",
            Text = "SnapLite is a small screenshot tool that runs in the tray, next to the clock.",
            Icon = new TaskDialogIcon(icon),
            Buttons = { install, justRun },
            Verification = startup,
            Footnote = new TaskDialogFootnote($"Installs to {ShortPath(InstallDir)}"),
            AllowCancel = true,
        };
        var result = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        return (result == install, startup.Checked);
    }

    private static bool AskUpdate(Version installed, Version current)
    {
        var update = new TaskDialogCommandLinkButton($"Update to {current.ToString(3)}", "Your settings are kept.");
        var keep = new TaskDialogCommandLinkButton($"Keep {installed.ToString(3)}", "Run this downloaded copy once without installing it.");
        using var icon = AppIcons.Create(paused: false, pixelSize: 32);
        var page = new TaskDialogPage
        {
            Caption = "SnapLite",
            Heading = "Update SnapLite?",
            Text = $"SnapLite {installed.ToString(3)} is installed on this PC. This file is version {current.ToString(3)}.",
            Icon = new TaskDialogIcon(icon),
            Buttons = { update, keep },
            AllowCancel = true,
        };
        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) == update;
    }

    // ---- Install ----------------------------------------------------------------------

    /// <summary>Copies this exe into place and registers it. Safe to run again (update/repair).</summary>
    public static void Install(bool startWithWindows, bool stopRunning = true)
    {
        string source = CurrentExe ?? throw new InvalidOperationException("Can't find this program's file.");
        if (stopRunning) StopRunningInstance(); // an installed copy that's running keeps its exe locked
        Directory.CreateDirectory(InstallDir);
        string temp = InstalledExe + ".new";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, InstalledExe, overwrite: true);

        CreateShortcut(StartMenuShortcut, InstalledExe, "Screenshots of regions, windows, whole webpages and steps");
        RegisterUninstall();
        if (startWithWindows) StartupRegistration.EnableFor(InstalledExe);
        // The browser extension's registration is re-pointed by the installed copy itself when it starts.
        AppLog.Info($"installed {CurrentVersion.ToString(3)} to {InstallDir}");
    }

    private static void RegisterUninstall()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "SnapLite");
        key.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        key.SetValue("Publisher", "SnapLite");
        key.SetValue("DisplayIcon", $"{InstalledExe},0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("URLInfoAbout", "https://github.com/claeslanng/SnapLite");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>A .lnk through the Windows Script Host COM object (always present on Windows).</summary>
    private static void CreateShortcut(string lnk, string target, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = description;
            link.IconLocation = $"{target},0";
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Set by "Install" in Settings: Program starts the installed copy after this one has exited.</summary>
    public static bool LaunchInstalledOnExit { get; set; }

    public static void LaunchInstalled()
    {
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir })?.Dispose();
    }

    /// <summary>Asks a running SnapLite to exit and waits (up to 5 s) until it has.</summary>
    public static void StopRunningInstance()
    {
        if (!Mutex.TryOpenExisting(Program.MutexName, out var m)) return;
        m.Dispose();
        IntPtr target = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, MessageWindow.WindowName);
        if (target != IntPtr.Zero) Native.PostMessage(target, Native.WM_SNAPLITE_EXIT, IntPtr.Zero, IntPtr.Zero);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (!Mutex.TryOpenExisting(Program.MutexName, out var still)) break;
            still.Dispose();
            Thread.Sleep(100);
        }
        // Its process may need a moment more to release the exe file.
        Thread.Sleep(300);
    }

    // ---- Uninstall (Settings › Apps › SnapLite › Uninstall) -------------------------------

    public static int RunUninstall(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        bool removeData = args.Contains("--remove-data");
        if (!quiet)
        {
            var remove = new TaskDialogButton("Uninstall");
            var data = new TaskDialogVerificationCheckBox("Also delete my settings");
            using var icon = AppIcons.Create(paused: false, pixelSize: 32);
            var page = new TaskDialogPage
            {
                Caption = "SnapLite",
                Heading = "Uninstall SnapLite?",
                Text = "Your screenshots in Pictures\\SnapLite are kept.",
                Icon = new TaskDialogIcon(icon),
                Buttons = { remove, TaskDialogButton.Cancel },
                Verification = data,
                DefaultButton = remove,
            };
            if (TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) != remove) return 1;
            removeData = data.Checked;
        }

        StopRunningInstance();
        try { StartupRegistration.Set(false); } catch { }
        try { BrowserSetup.Remove(); } catch { }
        TryDelete(StartMenuShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        if (removeData)
        {
            TryDeleteDir(SettingsStore.Folder);
            TryDeleteDir(AppLog.Folder);
        }

        // An exe can't delete itself while it runs: let a hidden command prompt remove the folder
        // a moment after this process has exited.
        if (IsRunningInstalledCopy)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        else
            TryDeleteDir(InstallDir);

        if (!quiet)
            MessageBox.Show("SnapLite was removed. Your screenshots in Pictures\\SnapLite were kept.", "SnapLite",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary><c>--install [--startup] [--no-launch]</c>: install without asking (for scripts and tests).</summary>
    public static int RunInstallCli(string[] args)
    {
        if (!IsSingleFile)
        {
            Console.Error.WriteLine("Only the published single-file SnapLite.exe can be installed.");
            return 2;
        }
        Install(args.Contains("--startup"));
        if (!args.Contains("--no-launch")) LaunchInstalled();
        return 0;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private static string ShortPath(string path)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path[local.Length..] : path;
    }
}
