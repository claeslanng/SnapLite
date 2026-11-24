using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SnapLite.WebCapture;

internal sealed class WebCaptureException(string message) : Exception(message);

/// <summary>Thin helpers over the Chrome DevTools Protocol exposed by WebView2.</summary>
internal static class Cdp
{
    public static async Task<JsonElement> CallAsync(CoreWebView2 core, string method, string paramsJson = "{}")
    {
        string result = await core.CallDevToolsProtocolMethodAsync(method, paramsJson);
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.Clone();
    }

    /// <summary>Builds a JSON object with a Utf8JsonWriter (no reflection-based serialization).</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Evaluates JavaScript and awaits promises (ExecuteScriptAsync can't). Returns the value
    /// as JSON, or throws <see cref="WebCaptureException"/> if the script threw.
    /// </summary>
    public static async Task<JsonElement> EvalAsync(CoreWebView2 core, string expression, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var r = await CallAsync(core, "Runtime.evaluate", Json(w =>
        {
            w.WriteString("expression", expression);
            w.WriteBoolean("awaitPromise", true);
            w.WriteBoolean("returnByValue", true);
        }));
        if (r.TryGetProperty("exceptionDetails", out var ex))
        {
            string msg = ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d)
                ? d.GetString() ?? "script error"
                : ex.GetProperty("text").GetString() ?? "script error";
            throw new WebCaptureException("Page script failed: " + msg.Split('\n')[0]);
        }
        return r.GetProperty("result").TryGetProperty("value", out var v) ? v : default;
    }
}
