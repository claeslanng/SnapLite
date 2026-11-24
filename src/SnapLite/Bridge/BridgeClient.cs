using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using SnapLite.Interop;

namespace SnapLite.Bridge;

/// <summary>
/// Talks to the running tray app over a named pipe that only the current user can open
/// (one JSON line in, one JSON line out). Used by the native messaging host.
/// </summary>
internal static class BridgeClient
{
    /// <summary>Per user, so two people signed in to one PC never reach each other's SnapLite.</summary>
    public static string PipeName { get; } = "SnapLite.Bridge." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>Images from the browser are written here by the host and deleted by the tray app after saving.</summary>
    public static string IncomingFolder => Path.Combine(AppLog.Folder, "incoming");

    public static bool IsInIncomingFolder(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetFullPath(IncomingFolder).TrimEnd('\\') + "\\";
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsTrayRunning()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(150);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sends one request; starts the tray app first if it isn't running.</summary>
    public static async Task<JsonObject> SendAsync(JsonObject request, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(400);
        }
        catch (TimeoutException)
        {
            // Running but busy, or still starting? Then just wait. Only start it if it really isn't running.
            if (!Mutex.TryOpenExisting(Program.MutexName, out var m)) StartTrayApp();
            else m.Dispose();
            await pipe.ConnectAsync(15000); // a cold start takes well under a second; be generous
        }

        var utf8 = new UTF8Encoding(false);
        await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
        await writer.WriteLineAsync(request.ToJsonString());
        using var cts = new CancellationTokenSource(timeout);
        string? line = await reader.ReadLineAsync(cts.Token);
        return (line == null ? null : JsonNode.Parse(line) as JsonObject)
               ?? throw new IOException("SnapLite closed the connection without answering.");
    }

    /// <summary>
    /// Started through Explorer so the tray app isn't a child of the browser's host process (the browser
    /// may end that process tree when the extension disconnects).
    /// </summary>
    private static void StartTrayApp()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find SnapLite.exe.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
    }
}
