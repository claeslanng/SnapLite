using System.Text;
using System.Text.Json.Nodes;
using SnapLite.Core.Bridge;
using SnapLite.Interop;

namespace SnapLite.Bridge;

/// <summary>
/// The native messaging host: Chrome/Edge start <c>SnapLite.exe chrome-extension://…/</c> when the
/// extension connects. It receives the captured image in chunks, writes it to a temporary file and
/// hands it to the tray app (which saves, copies and notifies exactly like any other capture).
///
/// Messages from the extension (JSON):
///   { type: "hello" }                                  → { type: "hello", app, version, protocol, running }
///   { type: "begin", id, meta: { kind, title, … } }    → { type: "ready", id }
///   { type: "chunk", id, data: base64 }                (no reply)
///   { type: "end", id }                                → { type: "done", id, ok, path | folder, count, error }
/// </summary>
internal static class NativeHost
{
    public const int Protocol = 1;

    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    private sealed class Transfer(string path, FileStream stream, JsonObject meta)
    {
        public string Path { get; } = path;
        public FileStream Stream { get; } = stream;
        public JsonObject Meta { get; } = meta;
    }

    public static int Run(string[] args)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var transfers = new Dictionary<string, Transfer>();
        try
        {
            while (NativeMessaging.Read(stdin) is { } message)
            {
                JsonObject? reply;
                string? id = null;
                try
                {
                    var msg = JsonNode.Parse(message) as JsonObject ?? throw new InvalidDataException("Not a JSON object.");
                    id = (string?)msg["id"];
                    reply = Handle(msg, transfers);
                }
                catch (Exception ex)
                {
                    AppLog.Error("native host", ex);
                    reply = new JsonObject { ["type"] = "done", ["id"] = id, ["ok"] = false, ["error"] = ex.Message };
                    if (id != null && transfers.Remove(id, out var t)) Discard(t);
                }
                if (reply != null) NativeMessaging.Write(stdout, Encoding.UTF8.GetBytes(reply.ToJsonString()));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("native host stream", ex);
        }
        finally
        {
            foreach (var t in transfers.Values) Discard(t); // the browser disconnected mid-transfer
        }
        return 0;
    }

    private static JsonObject? Handle(JsonObject msg, Dictionary<string, Transfer> transfers)
    {
        string type = (string?)msg["type"] ?? "";
        string? id = (string?)msg["id"];
        switch (type)
        {
            case "hello":
                return new JsonObject
                {
                    ["type"] = "hello",
                    ["app"] = "SnapLite",
                    ["version"] = AppLog.Version,
                    ["protocol"] = Protocol,
                    ["running"] = BridgeClient.IsTrayRunning(),
                };

            case "begin":
            {
                if (id == null) throw new InvalidDataException("begin without id");
                var meta = msg["meta"] as JsonObject ?? new JsonObject();
                Directory.CreateDirectory(BridgeClient.IncomingFolder);
                string path = Path.Combine(BridgeClient.IncomingFolder, $"{Guid.NewGuid():N}.png");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                transfers[id] = new Transfer(path, stream, (JsonObject)meta.DeepClone());
                return new JsonObject { ["type"] = "ready", ["id"] = id };
            }

            case "chunk":
            {
                var t = Get(transfers, id);
                t.Stream.Write(Convert.FromBase64String((string?)msg["data"] ?? ""));
                return null;
            }

            case "end":
            {
                var t = Get(transfers, id);
                transfers.Remove(id!);
                t.Stream.Dispose();
                var request = (JsonObject)t.Meta.DeepClone();
                request["cmd"] = "import";
                request["file"] = t.Path;
                JsonObject response;
                try
                {
                    // Big pages take a few seconds to encode and save.
                    response = BridgeClient.SendAsync(request, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
                }
                catch
                {
                    TryDelete(t.Path);
                    throw;
                }
                response["type"] = "done";
                response["id"] = id;
                return response;
            }

            case "cancel":
                if (id != null && transfers.Remove(id, out var c)) Discard(c);
                return null;

            default:
                throw new InvalidDataException($"Unknown message type \"{type}\".");
        }
    }

    private static Transfer Get(Dictionary<string, Transfer> transfers, string? id) =>
        id != null && transfers.TryGetValue(id, out var t) ? t : throw new InvalidDataException($"Unknown transfer \"{id}\".");

    private static void Discard(Transfer t)
    {
        try { t.Stream.Dispose(); } catch { }
        TryDelete(t.Path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
