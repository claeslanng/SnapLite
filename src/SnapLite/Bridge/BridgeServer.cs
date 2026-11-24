using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using SnapLite.Interop;

namespace SnapLite.Bridge;

/// <summary>
/// The tray app's end of the pipe. Started on the UI thread, so requests are handled there; all
/// waiting is asynchronous I/O (no thread, no CPU while idle). A new listener is opened as soon as a
/// client connects, so a long save never makes SnapLite look "not running".
/// </summary>
internal sealed class BridgeServer(Func<JsonObject, Task<JsonObject>> handler) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    public void Start() => _ = ListenAsync();

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(BridgeClient.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                AppLog.Error("bridge: couldn't create the pipe", ex);
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                if (ex is not OperationCanceledException) AppLog.Error("bridge", ex);
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            _ = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
            string? line = await reader.ReadLineAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            if (line == null) return; // an "are you running?" probe: connected and hung up

            JsonObject response;
            try
            {
                var request = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("Not a JSON object.");
                response = await handler(request);
            }
            catch (Exception ex)
            {
                AppLog.Error("bridge request", ex);
                response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
            await using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(response.ToJsonString());
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (!pipe.IsConnected) { } // the other side went away; nothing to answer
        catch (Exception ex) { AppLog.Error("bridge", ex); }
        finally { pipe.Dispose(); }
    }

    public void Dispose() => _cts.Cancel();
}
