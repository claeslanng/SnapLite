using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SnapLite.Core.Splitting;

namespace SnapLite.WebCapture;

/// <summary>How to split a page in Steps mode. SimpleHeight 0 = one viewport per step.</summary>
internal sealed record StepSettings(bool Smart, int MinHeight, int MaxHeight, int SimpleHeight, int SimpleOverlap);

internal sealed record WebCaptureOptions(
    string Url,
    int ViewportWidth,
    bool HideBanners,
    bool FixFloating,
    bool DisableAnimations,
    int SettleDelayMs,
    int MaxPageHeight);

/// <summary>
/// One hidden WebView2 page used for a single capture. Create, load, prepare, capture, dispose.
/// Nothing WebView2-related outlives this object, so idle memory stays low.
/// </summary>
internal sealed class WebPageSession : IDisposable
{
    /// <summary>Chromium can't reliably render one bitmap taller than ~16k px; above this we capture in tiles.</summary>
    public const int SingleShotLimit = 16000;
    private const int TileHeight = 8000;

    private const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

    // Keep rendering while the host window is off-screen, and keep the disk cache small.
    private const string BrowserArgs =
        "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling " +
        "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disk-cache-size=67108864";

    public static string UserDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapLite", "WebView2");

    private readonly OffscreenHost _host;
    private readonly WebView2 _view;
    private CoreWebView2 _core = null!;
    private NetworkTracker? _network;
    private bool _scriptInjected;

    public int ViewportWidth { get; }
    public int ViewportHeight { get; }
    public bool IsMobile => ViewportWidth < 600;
    public string Title { get; private set; } = "";
    public string FinalUrl { get; private set; } = "";
    public CoreWebView2 Core => _core;

    private WebPageSession(int viewportWidth)
    {
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportWidth < 600 ? 844 : 900;
        _host = new OffscreenHost(new Size(ViewportWidth, ViewportHeight));
        _view = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_view);
    }

    public static async Task<WebPageSession> CreateAsync(int viewportWidth, CancellationToken ct)
    {
        var s = new WebPageSession(viewportWidth);
        try
        {
            s._host.Show();
            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder,
                    new CoreWebView2EnvironmentOptions(BrowserArgs));
            }
            catch (WebView2RuntimeNotFoundException)
            {
                throw new WebCaptureException(
                    "The Microsoft Edge WebView2 Runtime is not installed. Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            }
            await s._view.EnsureCoreWebView2Async(env);
            ct.ThrowIfCancellationRequested();
            await s.ConfigureAsync();
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private async Task ConfigureAsync()
    {
        _core = _view.CoreWebView2;
        var st = _core.Settings;
        st.AreDefaultContextMenusEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreDefaultScriptDialogsEnabled = false; // alert()/confirm() must never block a capture
        st.IsZoomControlEnabled = false;
        _core.IsMuted = true;
        _core.ScriptDialogOpening += (_, e) => e.Accept();
        _core.NewWindowRequested += (_, e) => e.Handled = true; // no pop-up windows

        await Cdp.CallAsync(_core, "Emulation.setDeviceMetricsOverride", Cdp.Json(w =>
        {
            w.WriteNumber("width", ViewportWidth);
            w.WriteNumber("height", ViewportHeight);
            w.WriteNumber("deviceScaleFactor", 1);
            w.WriteBoolean("mobile", IsMobile);
        }));
        await Cdp.CallAsync(_core, "Emulation.setScrollbarsHidden", Cdp.Json(w => w.WriteBoolean("hidden", true)));
        // The host window never has focus; pretend it does so carousels/animations that pause on blur still run.
        await Cdp.CallAsync(_core, "Emulation.setFocusEmulationEnabled", Cdp.Json(w => w.WriteBoolean("enabled", true)));
        if (IsMobile)
        {
            await Cdp.CallAsync(_core, "Emulation.setUserAgentOverride", Cdp.Json(w => w.WriteString("userAgent", MobileUserAgent)));
            await Cdp.CallAsync(_core, "Emulation.setTouchEmulationEnabled", Cdp.Json(w =>
            {
                w.WriteBoolean("enabled", true);
                w.WriteNumber("maxTouchPoints", 5);
            }));
        }
        _network = await NetworkTracker.StartAsync(_core);
    }

    /// <summary>Navigates and waits for the load event plus network idle.</summary>
    public async Task LoadAsync(string url, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Loading page…");
        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e);
        _core.NavigationCompleted += OnCompleted;
        try
        {
            _core.Navigate(url);
            var done = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
            if (!done.IsSuccess && done.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                throw new WebCaptureException($"The page could not be loaded ({FriendlyError(done.WebErrorStatus)}).");
        }
        catch (TimeoutException)
        {
            _core.Stop(); // a slow third-party resource: capture what has loaded
        }
        finally
        {
            _core.NavigationCompleted -= OnCompleted;
        }

        progress?.Report("Waiting for the page to settle…");
        await _network!.WaitForIdleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), ct);
        FinalUrl = _core.Source;
    }

    /// <summary>Clean-up, lazy-load scroll, image wait, settle delay.</summary>
    public async Task PrepareAsync(WebCaptureOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        await EnsureScriptAsync(ct);
        if (o.DisableAnimations) await Cdp.EvalAsync(_core, "__snaplite.disableAnimations()", ct);
        if (o.HideBanners) await Cdp.EvalAsync(_core, "__snaplite.hideBanners()", ct);

        progress?.Report("Scrolling to load lazy images…");
        await Cdp.EvalAsync(_core, $"__snaplite.autoScroll({o.MaxPageHeight}, 20000)", ct);
        await Cdp.EvalAsync(_core, "__snaplite.waitForImages(6000)", ct);
        await _network!.WaitForIdleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(6), ct);

        await EnsureScriptAsync(ct); // a client-side redirect may have replaced the document
        if (o.HideBanners) await Cdp.EvalAsync(_core, "__snaplite.hideBanners()", ct); // banners that showed up late
        if (o.FixFloating) await Cdp.EvalAsync(_core, "__snaplite.fixFloating()", ct);
        await Cdp.EvalAsync(_core, "__snaplite.expandInnerScroller()", ct);

        if (o.SettleDelayMs > 0) await Task.Delay(o.SettleDelayMs, ct);

        var info = await Cdp.EvalAsync(_core, "__snaplite.pageInfo()", ct);
        Title = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
    }

    private async Task EnsureScriptAsync(CancellationToken ct)
    {
        var present = await Cdp.EvalAsync(_core, "typeof window.__snaplite === 'object'", ct);
        if (_scriptInjected && present.ValueKind == JsonValueKind.True) return;
        await Cdp.EvalAsync(_core, PageScripts.Cleanup, ct);
        await Cdp.EvalAsync(_core, PageScripts.Sections, ct);
        _scriptInjected = true;
    }

    /// <summary>
    /// Measures section edges, headings and content zones (text lines, images) for the step splitter.
    /// Call after <see cref="PrepareAsync"/> and before capturing (the page must be scrolled to the top).
    /// </summary>
    public async Task<PageLayout> MeasureLayoutAsync(int maxStepHeight, CancellationToken ct)
    {
        await EnsureScriptAsync(ct);
        await Cdp.EvalAsync(_core, "window.scrollTo(0,0)", ct);
        var m = await Cdp.EvalAsync(_core, $"__snaplite.measureLayout({maxStepHeight})", ct);
        return PageLayoutJson.Parse(m);
    }

    /// <summary>Full page size in CSS px (= image px, since deviceScaleFactor is 1).</summary>
    public async Task<Size> GetPageSizeAsync()
    {
        var m = await Cdp.CallAsync(_core, "Page.getLayoutMetrics");
        var content = m.TryGetProperty("cssContentSize", out var c) ? c : m.GetProperty("contentSize");
        int height = (int)Math.Ceiling(content.GetProperty("height").GetDouble());
        int width = m.TryGetProperty("cssLayoutViewport", out var lv)
            ? (int)lv.GetProperty("clientWidth").GetDouble()
            : ViewportWidth;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// Captures the whole page.
    /// Default: scroll the real 900 px viewport and stitch frames (fast on every page we measured;
    /// on a heavy Wikipedia article 2.7 s vs 57 s for CDP captureBeyondViewport, which re-lays out
    /// the whole page on every call).
    /// Fallback, for pages that won't scroll (scroll hijacking, inner scroll areas): CDP
    /// captureBeyondViewport, as one shot up to 16,000 px, otherwise in stitched tiles.
    /// </summary>
    public async Task<(Bitmap Image, bool Truncated)> CaptureFullPageAsync(int maxHeight, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Capturing…");
        Size page = await GetPageSizeAsync();
        bool truncated = page.Height > maxHeight;
        int height = Math.Min(page.Height, maxHeight);
        int width = page.Width;

        if (height > ViewportHeight)
        {
            var scrolled = await CaptureByScrollingAsync(width, height, progress, ct);
            if (scrolled != null) return (scrolled, truncated);
        }

        if (height <= SingleShotLimit)
            return (await CaptureClipAsync(new Rectangle(0, 0, width, height), ct), truncated);

        var canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            canvas.SetResolution(96, 96);
            using var g = Graphics.FromImage(canvas);
            for (int y = 0; y < height; y += TileHeight)
            {
                int h = Math.Min(TileHeight, height - y);
                progress?.Report($"Capturing… {y * 100 / height}%");
                using var tile = await CaptureClipAsync(new Rectangle(0, y, width, h), ct);
                g.DrawImage(tile, new Rectangle(0, y, width, h), new Rectangle(0, 0, tile.Width, Math.Min(h, tile.Height)), GraphicsUnit.Pixel);
            }
            return (canvas, truncated);
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures by scrolling the normal viewport and stitching viewport-sized frames. After the first
    /// frame, fixed elements are hidden and sticky ones put back in flow (re-checked before every frame,
    /// for headers that only appear once you scroll), so they show up only once.
    /// Returns null if the page doesn't actually scroll, so the caller can use the CDP fallback.
    /// </summary>
    public async Task<Bitmap?> CaptureByScrollingAsync(int width, int height, IProgress<string>? progress, CancellationToken ct)
    {
        var canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            canvas.SetResolution(96, 96);
            using var g = Graphics.FromImage(canvas);
            for (int y = 0; y < height; y += ViewportHeight)
            {
                progress?.Report($"Capturing… {y * 100 / height}%");
                // Only touch fixed elements once the page really scrolled, so the CDP fallback still sees them.
                string hideFixed = y > 0 ? "if(window.scrollY>0){__snaplite.fixedAfterFirstScreen();await f();}" : "";
                var actual = await Cdp.EvalAsync(_core,
                    "(async()=>{const f=()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));" +
                    $"window.scrollTo(0,{y});await f();{hideFixed}return Math.round(window.scrollY);}})()", ct);
                int scrolledTo = actual.ValueKind == JsonValueKind.Number ? actual.GetInt32() : y;
                if (y > 0 && scrolledTo == 0)
                {
                    canvas.Dispose();
                    await Cdp.EvalAsync(_core, "window.scrollTo(0,0)", ct);
                    return null; // the document doesn't scroll
                }
                using var frame = await CaptureViewportAsync(ct);

                int srcY = Math.Max(0, y - scrolledTo); // at the bottom the page can't scroll all the way
                int h = Math.Min(Math.Min(ViewportHeight, height - y), frame.Height - srcY);
                if (h > 0)
                    g.DrawImage(frame, new Rectangle(0, y, Math.Min(width, frame.Width), h),
                        new Rectangle(0, srcY, Math.Min(width, frame.Width), h), GraphicsUnit.Pixel);
            }
            await Cdp.EvalAsync(_core, "window.scrollTo(0,0)", ct);
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    private async Task<Bitmap> CaptureViewportAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var r = await Cdp.CallAsync(_core, "Page.captureScreenshot", Cdp.Json(w =>
        {
            w.WriteString("format", "png");
            w.WriteBoolean("fromSurface", true);
        }));
        return DecodePng(Convert.FromBase64String(r.GetProperty("data").GetString()!));
    }

    public async Task<Bitmap> CaptureClipAsync(Rectangle clip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var r = await Cdp.CallAsync(_core, "Page.captureScreenshot", Cdp.Json(w =>
        {
            w.WriteString("format", "png");
            w.WriteBoolean("captureBeyondViewport", true);
            w.WriteBoolean("fromSurface", true);
            w.WriteStartObject("clip");
            w.WriteNumber("x", clip.X);
            w.WriteNumber("y", clip.Y);
            w.WriteNumber("width", clip.Width);
            w.WriteNumber("height", clip.Height);
            w.WriteNumber("scale", 1);
            w.WriteEndObject();
        }));
        byte[] png = Convert.FromBase64String(r.GetProperty("data").GetString()!);
        return DecodePng(png);
    }

    /// <summary>GDI+ needs the stream alive for a Bitmap's lifetime, so decode into an independent copy.</summary>
    private static Bitmap DecodePng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var tmp = new Bitmap(ms);
        var copy = tmp.Clone(new Rectangle(0, 0, tmp.Width, tmp.Height), PixelFormat.Format32bppRgb);
        copy.SetResolution(96, 96);
        return copy;
    }

    private static string FriendlyError(CoreWebView2WebErrorStatus s) => s switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "the address could not be found",
        CoreWebView2WebErrorStatus.CannotConnect => "could not connect",
        CoreWebView2WebErrorStatus.Timeout => "timed out",
        CoreWebView2WebErrorStatus.Disconnected => "no internet connection",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or
        CoreWebView2WebErrorStatus.CertificateRevoked => "certificate problem",
        _ => s.ToString(),
    };

    public void Dispose()
    {
        _network?.Dispose();
        _view.Dispose();   // closes the browser process for this page
        _host.Dispose();
    }
}

/// <summary>The M3 flow: load → prepare → full-page capture.</summary>
internal static class WebPageCapturer
{
    public sealed record Result(Bitmap Image, string Title, bool Truncated, string FinalUrl);

    public static async Task<Result> CaptureAsync(WebCaptureOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        using var session = await WebPageSession.CreateAsync(o.ViewportWidth, ct);
        await session.LoadAsync(o.Url, progress, ct);
        await session.PrepareAsync(o, progress, ct);
        var (image, truncated) = await session.CaptureFullPageAsync(o.MaxPageHeight, progress, ct);
        return new Result(image, session.Title, truncated, session.FinalUrl);
    }

    public sealed record StepsResult(Bitmap FullImage, IReadOnlyList<Step> Steps, string Title, bool Truncated, string FinalUrl, bool Smart);

    /// <summary>The M4 flow: load → prepare → measure layout → full-page capture → split into steps.</summary>
    public static async Task<StepsResult> CaptureStepsAsync(WebCaptureOptions o, StepSettings s, IProgress<string>? progress, CancellationToken ct)
    {
        using var session = await WebPageSession.CreateAsync(o.ViewportWidth, ct);
        await session.LoadAsync(o.Url, progress, ct);
        await session.PrepareAsync(o, progress, ct);
        progress?.Report("Finding sections…");
        var layout = await session.MeasureLayoutAsync(s.MaxHeight, ct);
        var (image, truncated) = await session.CaptureFullPageAsync(o.MaxPageHeight, progress, ct);

        // Measured and captured heights can differ by a few px; the image is the truth.
        layout = layout with { Height = image.Height };
        int simpleChunk = s.SimpleHeight > 0 ? s.SimpleHeight : session.ViewportHeight;
        var steps = s.Smart
            ? StepSplitter.SmartSplit(layout, s.MinHeight, s.MaxHeight)
            : StepSplitter.SimpleSplit(image.Height, simpleChunk, s.SimpleOverlap, layout.Headings);
        return new StepsResult(image, steps, session.Title, truncated, session.FinalUrl, s.Smart);
    }

    /// <summary>Accepts "example.com/page" as well as full URLs. Returns null if it isn't a usable address.</summary>
    public static string? NormalizeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Contains(' ') || text.Contains('\n')) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (!text.Contains('.') && !text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return null;
            text = (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://") + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file"
            ? uri.AbsoluteUri
            : null;
    }
}
