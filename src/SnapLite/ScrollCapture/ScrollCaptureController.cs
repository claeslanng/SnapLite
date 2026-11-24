using SnapLite.Capture;
using SnapLite.Core.Imaging;
using SnapLite.Core.Stitching;
using SnapLite.Interop;

namespace SnapLite.ScrollCapture;

internal sealed record ScrollCaptureOptions(int MaxHeight, bool StartFromTop, int SettleDelayMs);

/// <summary>
/// Best-effort scroll capture of any window: scrolls it with the mouse wheel, screenshots each
/// position and stitches the frames with <see cref="ScrollStitcher"/> (row-hash overlap matching,
/// sticky header/footer and static side-panel removal).
/// </summary>
internal static class ScrollCaptureController
{
    /// <summary>Image is null when the capture stopped before the first screen (Note says why).</summary>
    public sealed record Result(Bitmap? Image, string Title, string? Note);

    private const int MaxFrames = 400;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(2);

    /// <summary>The window under the cursor, or (from the tray menu) the topmost app window.</summary>
    public static IntPtr PickTarget(bool underCursor)
    {
        if (underCursor)
        {
            var p = Cursor.Position;
            IntPtr h = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y }), Native.GA_ROOT);
            if (WindowCapture.IsCandidate(h)) return h;
        }
        return WindowCapture.FindTargetWindow();
    }

    public static async Task<Result?> RunAsync(IntPtr hwnd, ScrollCaptureOptions o)
    {
        var log = new ScrollLog();
        DeleteOldFailurePair();
        Rectangle rect = WindowCapture.GetVisibleBounds(hwnd);
        rect.Intersect(Screen.FromRectangle(rect).Bounds); // only the part on the window's own monitor
        string title = Native.GetWindowText(hwnd);
        log.Add($"target 0x{hwnd:X} class={Native.GetClassName(hwnd)} title=\"{title}\" rect={rect} options={o}");
        if (rect.Width < 50 || rect.Height < 50) { log.Add("window too small"); log.Save(); return null; }

        bool fg = Native.SetForegroundWindow(hwnd); // nothing may cover it while we screenshot
        var scrollPoint = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        var savedCursor = Cursor.Position;
        log.Add($"SetForegroundWindow={fg} scrollPoint={scrollPoint} cursor={savedCursor}");

        using var indicator = new ScrollIndicatorForm(rect, scrollPoint);
        indicator.Show();
        var ct = indicator.Token;
        var started = DateTime.UtcNow;
        string? note = null;
        var stitcher = new ScrollStitcher(o.MaxHeight);
        (PixelBuffer A, PixelBuffer B)? sample = null;

        try
        {
            await Task.Delay(200, ct);
            // Screenshots copy the screen, so the target must really be in front. Windows can refuse
            // SetForegroundWindow; never capture whatever happens to cover the window instead.
            if (!IsFrontAt(hwnd, scrollPoint))
            {
                log.Add($"not in front: window at point is 0x{RootAt(scrollPoint):X} ({Native.GetClassName(RootAt(scrollPoint))})");
                throw new InvalidOperationException("Couldn't bring that window to the front. Click it once, then press the shortcut again.");
            }
            Cursor.Position = scrollPoint;
            log.Add($"cursor set, reads back {Cursor.Position}");

            if (o.StartFromTop) log.Add($"scroll to top: {await ScrollToTopAsync(rect, o, log, ct)} wheel steps");

            var first = await GrabSettledAsync(rect, null, o.SettleDelayMs, ct);
            log.Add($"first frame {first.Width}x{first.Height}: {stitcher.Add(first).Status}");
            var prevFrame = first;
            int notches = 2, noChange = 0, appended = 0;
            bool everScrolled = false;

            for (int i = 0; i < MaxFrames; i++)
            {
                if (ct.IsCancellationRequested) { note = "Stopped with Esc"; break; }
                if (Cursor.Position != scrollPoint) { note = "Stopped because the mouse moved"; log.Add($"cursor now {Cursor.Position}"); break; }
                if (DateTime.UtcNow - started > MaxDuration) { note = "Stopped after 2 minutes"; break; }
                if (!IsFrontAt(hwnd, scrollPoint)) { note = "Stopped because another window came in front"; break; }

                Native.MouseWheel(-notches);
                var frame = await GrabSettledAsync(rect, prevFrame, o.SettleDelayMs, ct);
                double changed = FrameAnalysis.ChangedFraction(prevFrame, frame);
                var accepted = stitcher.LastFrame;
                var r = stitcher.Add(frame);
                log.Add($"step {i + 1}: wheel -{notches} changed={changed:P1} -> {r.Status} dy={r.Dy} height={stitcher.Height} " +
                        $"header={stitcher.HeaderHeight} footer={stitcher.FooterHeight} cols={stitcher.OutputColumns}");

                // Lazy-loaded images, smooth scrolling and layout shifts often finish a moment later:
                // look again before giving up.
                for (int retry = 1; r.Status == StitchStatus.NoMatch && retry <= 2; retry++)
                {
                    await Task.Delay(400 * retry, ct);
                    var again = await GrabSettledAsync(rect, null, 0, ct);
                    r = stitcher.Add(again);
                    log.Add($"  retry {retry}: changed since first look {FrameAnalysis.ChangedFraction(frame, again):P1} -> {r.Status} dy={r.Dy}");
                    frame = again;
                }
                if (r.Status == StitchStatus.NoMatch && accepted != null) SaveFailurePair(accepted, frame, log);
                prevFrame = frame;

                if (r.Status == StitchStatus.Appended)
                {
                    log.Add($"  moving cols {Runs(stitcher.LastMovingColumns)} | sticky cols {Runs(stitcher.LastStickyColumns)}");
                    if (++appended == 3 && accepted != null) sample = (accepted, frame); // saved at the end for analysis
                    everScrolled = true;
                    noChange = 0;
                    indicator.SetText(stitcher.Height);
                    // Aim for ~60% of the visible body per step: plenty of overlap, not too many frames.
                    int body = frame.Height - stitcher.HeaderHeight - stitcher.FooterHeight;
                    notches = Math.Clamp((int)Math.Round(notches * (body * 0.6) / Math.Max(1, r.Dy)), 1, 25);
                }
                else if (r.Status == StitchStatus.NoChange)
                {
                    if (++noChange >= 2) break; // end of the page
                }
                else if (r.Status == StitchStatus.NoMatch)
                {
                    note = "Stopped early: two screens didn't line up (animated content, or it scrolled too far)";
                    break;
                }
                else if (r.Status == StitchStatus.MaxHeightReached)
                {
                    note = $"Reached the maximum height ({o.MaxHeight:N0} px)";
                    break;
                }
            }
            if (!everScrolled && note == null) note = "The window didn't scroll; saved a single screen";
        }
        catch (OperationCanceledException)
        {
            note = "Stopped with Esc";
        }
        catch (Exception ex)
        {
            log.Add("error: " + ex.Message);
            throw;
        }
        finally
        {
            Cursor.Position = savedCursor;
            if (stitcher.VotePairs > 0) log.Add($"column votes over {stitcher.VotePairs} steps, kept: {Runs(stitcher.OutputMask())}");
            if (sample is { } s) SavePair(s.A, s.B, "scroll-sample", log);
            log.Add($"done:note=\"{note}\" height={stitcher.Height} truncated={stitcher.Truncated} in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
            log.Save();
        }

        // Always report back, even when nothing was captured (e.g. Esc while scrolling to the top).
        if (stitcher.Height == 0) return new Result(null, title, (note ?? "Stopped") + " before anything was captured");
        return new Result(ImageConvert.ToBitmap(stitcher.Compose()), title, note);
    }

    private static string DiagnosticsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapLite");

    /// <summary>
    /// When two screens can't be lined up, keep them next to the log (overwritten every time, deleted
    /// at the start of the next capture) so the failure can be analysed. They never leave this PC.
    /// </summary>
    private static void SaveFailurePair(PixelBuffer before, PixelBuffer after, ScrollLog log) =>
        SavePair(before, after, "scroll-fail", log);

    private static void SavePair(PixelBuffer before, PixelBuffer after, string name, ScrollLog log)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsFolder);
            using (var a = ImageConvert.ToBitmap(before)) a.Save(Path.Combine(DiagnosticsFolder, name + "-1.png"), System.Drawing.Imaging.ImageFormat.Png);
            using (var b = ImageConvert.ToBitmap(after)) b.Save(Path.Combine(DiagnosticsFolder, name + "-2.png"), System.Drawing.Imaging.ImageFormat.Png);
            log.Add($"  saved two screens: {name}-1.png, {name}-2.png");
        }
        catch { /* diagnostics only */ }
    }

    private static void DeleteOldFailurePair()
    {
        foreach (var name in new[] { "scroll-fail-1.png", "scroll-fail-2.png", "scroll-sample-1.png", "scroll-sample-2.png" })
            try { File.Delete(Path.Combine(DiagnosticsFolder, name)); } catch { }
    }

    /// <summary>Compact "x0-x1" list of the runs of true columns (gaps of up to 3 bridged, runs under 4 px dropped).</summary>
    private static string Runs(bool[]? flags)
    {
        if (flags == null) return "-";
        var parts = new List<string>();
        int start = -1, last = -1;
        for (int x = 0; x <= flags.Length; x++)
        {
            bool on = x < flags.Length && flags[x];
            if (on && start >= 0 && x - last - 1 > 3)
            {
                if (last + 1 - start >= 4) parts.Add($"{start}-{last + 1}");
                start = -1;
            }
            if (on) { if (start < 0) start = x; last = x; }
        }
        if (start >= 0 && last + 1 - start >= 4) parts.Add($"{start}-{last + 1}");
        return parts.Count == 0 ? "none" : string.Join(",", parts.Take(40)) + (parts.Count > 40 ? $",... ({parts.Count} runs)" : "");
    }

    private static IntPtr RootAt(Point p) =>
        Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y }), Native.GA_ROOT);

    /// <summary>True if <paramref name="hwnd"/> is the top-level window the user would see at <paramref name="p"/>.</summary>
    private static bool IsFrontAt(IntPtr hwnd, Point p) => RootAt(p) == hwnd;

    /// <summary>Wheels up until the window stops changing. Returns how many wheel steps it took.</summary>
    private static async Task<int> ScrollToTopAsync(Rectangle rect, ScrollCaptureOptions o, ScrollLog log, CancellationToken ct)
    {
        PixelBuffer? last = null;
        var start = DateTime.UtcNow;
        for (int i = 0; i < 40; i++)
        {
            Native.MouseWheel(20);
            var f = await GrabSettledAsync(rect, last, o.SettleDelayMs, ct);
            double changed = last == null ? 1 : FrameAnalysis.ChangedFraction(last, f);
            log.Add($"  to top {i + 1}: changed {changed:P1}");
            // Didn't really move (only a blinking cursor or a small animation changed): we're at the top.
            if (changed < StillThreshold) return i + 1;
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(10)) { log.Add("  to top: gave up after 10 s, starting here"); return i + 1; }
            last = f;
        }
        return 40;
    }

    /// <summary>Below this share of changed screen, the window counts as not moving.</summary>
    private const double StillThreshold = 0.01;

    /// <summary>
    /// What happened during the last scroll capture, written to %LOCALAPPDATA%\SnapLite\last-scroll-capture.log
    /// (overwritten each time). Best-effort mode fails in app-specific ways; this makes them diagnosable.
    /// </summary>
    private sealed class ScrollLog
    {
        private readonly System.Text.StringBuilder _sb = new();
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public void Add(string line) => _sb.Append($"{_sw.ElapsedMilliseconds,6} ms  ").AppendLine(line);

        public void Save()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapLite");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "last-scroll-capture.log"), _sb.ToString());
            }
            catch { /* diagnostics only */ }
        }
    }

    /// <summary>
    /// Screenshots until the window has stopped moving (smooth scrolling, lazy rendering): two grabs
    /// in a row that are practically the same (under 1% changed, so a blinking cursor or a small
    /// animation doesn't count) after it moved away from <paramref name="previous"/>, or no real
    /// movement at all for 500 ms.
    /// </summary>
    private static async Task<PixelBuffer> GrabSettledAsync(Rectangle rect, PixelBuffer? previous, int delayMs, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        var f = Grab(rect);
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < 1500)
        {
            await Task.Delay(60, ct);
            var g = Grab(rect);
            if (FrameAnalysis.ChangedFraction(f, g) < StillThreshold)
            {
                bool moved = previous == null || FrameAnalysis.ChangedFraction(previous, g) >= StillThreshold;
                if (moved || (DateTime.UtcNow - start).TotalMilliseconds > 500) return g;
            }
            f = g;
        }
        return f;
    }

    private static PixelBuffer Grab(Rectangle rect)
    {
        using var bmp = ScreenCapture.Capture(rect);
        return ImageConvert.ToPixelBuffer(bmp);
    }
}
