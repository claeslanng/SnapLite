using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SnapLite.WebCapture;

/// <summary>
/// Counts in-flight requests via CDP Network events, to wait for "network idle".
/// Like Puppeteer's networkidle2: idle = at most 2 open requests for a quiet period
/// (analytics beacons and long-polling connections never finish).
/// </summary>
internal sealed class NetworkTracker : IDisposable
{
    private readonly HashSet<string> _inFlight = new();
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subs = new();
    private long _lastChange = Environment.TickCount64;

    private NetworkTracker() { }

    public static async Task<NetworkTracker> StartAsync(CoreWebView2 core)
    {
        var t = new NetworkTracker();
        t.Subscribe(core, "Network.requestWillBeSent", add: true);
        t.Subscribe(core, "Network.loadingFinished", add: false);
        t.Subscribe(core, "Network.loadingFailed", add: false);
        await Cdp.CallAsync(core, "Network.enable");
        return t;
    }

    private void Subscribe(CoreWebView2 core, string evt, bool add)
    {
        var receiver = core.GetDevToolsProtocolEventReceiver(evt);
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, e) =>
        {
            string? id = RequestId(e.ParameterObjectAsJson);
            if (id == null) return;
            bool changed = add ? _inFlight.Add(id) : _inFlight.Remove(id);
            if (changed) _lastChange = Environment.TickCount64;
        };
        receiver.DevToolsProtocolEventReceived += handler;
        _subs.Add((receiver, handler));
    }

    private static string? RequestId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task WaitForIdleAsync(TimeSpan quiet, TimeSpan max, CancellationToken ct)
    {
        long start = Environment.TickCount64;
        while (true)
        {
            long now = Environment.TickCount64;
            if (_inFlight.Count <= 2 && now - _lastChange >= quiet.TotalMilliseconds) return;
            if (now - start >= max.TotalMilliseconds) return; // give up waiting; capture what we have
            await Task.Delay(100, ct);
        }
    }

    public void Dispose()
    {
        foreach (var (r, h) in _subs) r.DevToolsProtocolEventReceived -= h;
        _subs.Clear();
    }
}
