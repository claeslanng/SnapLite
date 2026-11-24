using System.Diagnostics;
using SnapLite.Output;
using SnapLite.Settings;

namespace SnapLite.WebCapture;

/// <summary>
/// Command-line web capture (runs without the tray, alongside a running instance):
///   SnapLite.exe --capture-url https://example.com --out page.png [--width 1440] [--no-cleanup]
/// Exit code 0 on success. Messages go to stdout/stderr when redirected.
/// </summary>
internal static class WebCaptureCli
{
    public static bool IsCliInvocation(string[] args) => args.Length > 0 && args[0] == "--capture-url";

    public static int Run(string[] args)
    {
        string? url = null, outPath = null, steps = null;
        int? width = null;
        bool cleanup = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture-url" when i + 1 < args.Length: url = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--width" when i + 1 < args.Length && int.TryParse(args[i + 1], out int w): width = w; i++; break;
                case "--no-cleanup": cleanup = false; break;
                case "--steps" when i + 1 < args.Length: steps = args[++i].ToLowerInvariant(); break;
            }
        }

        url = WebPageCapturer.NormalizeUrl(url);
        if (url == null || string.IsNullOrWhiteSpace(outPath) || steps is not (null or "smart" or "simple"))
        {
            Console.Error.WriteLine("Usage: SnapLite.exe --capture-url <url> --out <file.png | folder> [--width 1280] [--no-cleanup] [--steps smart|simple]");
            return 2;
        }

        AppSettings s = SettingsStore.Load(out _, out _);
        var options = new WebCaptureOptions(url, Math.Clamp(width ?? s.WebViewportWidth, 320, 3840),
            cleanup && s.WebHideBanners, cleanup && s.WebFixFloating, cleanup && s.WebDisableAnimations,
            s.WebSettleDelayMs, s.WebMaxPageHeight);

        int exitCode = 1;
        var context = new ApplicationContext();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<string>(m => Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {m}"));

        async void Start()
        {
            try
            {
                if (steps != null)
                {
                    // --out is the folder in which the "Title_date" step folder is created.
                    var sr = await WebPageCapturer.CaptureStepsAsync(options, s.ToStepSettings() with { Smart = steps == "smart" },
                        progress, CancellationToken.None);
                    using (sr.FullImage)
                    {
                        var saved = await StepsWriter.SaveAsync(sr.FullImage, sr.Steps, sr.Title, sr.FinalUrl, sr.Smart,
                            s, Path.GetFullPath(outPath), DateTime.Now);
                        Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] Saved {saved.Count} steps from {sr.FullImage.Width}x{sr.FullImage.Height} -> {saved.Folder}");
                        foreach (var st in sr.Steps)
                            Console.WriteLine($"    {st.Index,2}: {st.Top,6}-{st.Bottom,-6} {st.Height,5} px  {st.Heading}");
                    }
                    exitCode = 0;
                    return;
                }

                var result = await WebPageCapturer.CaptureAsync(options, progress, CancellationToken.None);
                using (result.Image)
                {
                    string full = Path.GetFullPath(outPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    bool jpeg = full.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllBytes(full, OutputPipeline.Encode(result.Image, jpeg ? ImageFormatKind.Jpeg : ImageFormatKind.Png, s.JpegQuality));
                    Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] Saved {result.Image.Width}x{result.Image.Height}{(result.Truncated ? " (truncated)" : "")} \"{result.Title}\" -> {full}");
                }
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
            }
            finally
            {
                context.ExitThread();
            }
        }

        // WebView2 needs a running message loop; start once it is pumping.
        void OnIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnIdle;
            Start();
        }
        Application.Idle += OnIdle;
        Application.Run(context);
        return exitCode;
    }
}
