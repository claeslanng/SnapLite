using SnapLite.Bridge;
using SnapLite.Interop;
using SnapLite.Tray;
using SnapLite.WebCapture;

namespace SnapLite;

internal static class Program
{
    // Local\ = per logon session, so two users on one PC each get their own instance.
    internal const string MutexName = @"Local\SnapLite.SingleInstance.5b1f7c2e";

    [STAThread]
    private static int Main(string[] args)
    {
        // Started by Chrome/Edge for the browser extension: relay to the tray app, no UI.
        if (NativeHost.IsInvocation(args)) return NativeHost.Run(args);

        if (WebCaptureCli.IsCliInvocation(args))
        {
            ApplicationConfiguration.Initialize();
            return WebCaptureCli.Run(args);
        }

        if (args.Length > 0 && args[0] == "--toast-preview")
        {
            ApplicationConfiguration.Initialize();
            return ToastForm.RenderPreview(args);
        }
        if (args.Length > 0 && args[0] == "--render-docs")
        {
            ApplicationConfiguration.Initialize();
            return DocShots.Run(args);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // Unexpected errors: logged to %LOCALAPPDATA%\SnapLite\snaplite.log and shown as a tray
        // notification; the app keeps running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI thread", e.Exception);
            TrayContext.ReportUnexpected(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("background task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("fatal", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        try
        {
            using var context = new TrayContext();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            AppLog.Error("startup", ex);
            MessageBox.Show($"SnapLite couldn't start: {ex.Message}\n\nDetails: {AppLog.FilePath}", "SnapLite",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>Tells the already-running instance to show itself, then exits.</summary>
    private static void SignalExistingInstance()
    {
        IntPtr target = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, MessageWindow.WindowName);
        if (target == IntPtr.Zero) return;
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Native.PostMessage(target, Native.WM_SNAPLITE_SHOW, IntPtr.Zero, IntPtr.Zero);
    }
}
