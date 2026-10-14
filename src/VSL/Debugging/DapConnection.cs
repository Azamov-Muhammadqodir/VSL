using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSL.Lsp;

namespace VSL.Debugging;

public sealed class DapException(string command, string message) : Exception($"{command}: {message}")
{
    public string Command { get; } = command;
}

/// <summary>Debug Adapter Protocol client: seq-numbered requests, responses and events over LSP-style framing.</summary>
public sealed class DapConnection : IAsyncDisposable
{
    readonly MessageFraming _framing;
    readonly ConcurrentDictionary<int, (string Command, TaskCompletionSource<JsonElement> Tcs)> _pending = new();
    readonly CancellationTokenSource _cts = new();
    int _seq;
    Task? _readLoop;

    public DapConnection(Stream input, Stream output) => _framing = new MessageFraming(input, output);

    /// <summary>Raised on the reader thread for every event (name, body).</summary>
    public event Action<string, JsonElement>? EventReceived;

    public event Action<Exception?>? Disconnected;

    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = (command, tcs);
        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(seq, out var p))
                p.Tcs.TrySetCanceled(ct);
        });
        var message = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null)
            message["arguments"] = JsonSerializer.SerializeToNode(arguments, arguments.GetType(), JsonRpcConnection.JsonOptions);
        await _framing.WriteAsync(message.ToJsonString(JsonRpcConnection.JsonOptions)).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var buffer = await _framing.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (buffer is null)
                    break;
                using var doc = JsonDocument.Parse(buffer);
                Dispatch(doc.RootElement.Clone());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        foreach (var (_, p) in _pending)
            p.Tcs.TrySetException(new IOException("Debug adapter connection closed.", error));
        _pending.Clear();
        Disconnected?.Invoke(error);
    }

    void Dispatch(JsonElement message)
    {
        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "response":
                if (!_pending.TryRemove(message.GetProperty("request_seq").GetInt32(), out var pending))
                    return;
                if (message.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    pending.Tcs.TrySetResult(message.TryGetProperty("body", out var body) ? body : default);
                }
                else
                {
                    var text = message.TryGetProperty("message", out var m) ? m.GetString() : null;
                    if (message.TryGetProperty("body", out var b) && b.TryGetProperty("error", out var e) && e.TryGetProperty("format", out var f))
                        text = f.GetString();
                    pending.Tcs.TrySetException(new DapException(pending.Command, text ?? "request failed"));
                }
                break;
            case "event":
                try
                {
                    EventReceived?.Invoke(message.GetProperty("event").GetString()!, message.TryGetProperty("body", out var eb) ? eb : default);
                }
                catch
                {
                    // A faulty event handler must not kill the read loop.
                }
                break;
            case "request":
                // Reverse requests (runInTerminal, startDebugging) are not supported; tell the adapter so.
                var reply = new JsonObject
                {
                    ["seq"] = Interlocked.Increment(ref _seq),
                    ["type"] = "response",
                    ["request_seq"] = message.GetProperty("seq").GetInt32(),
                    ["command"] = message.GetProperty("command").GetString(),
                    ["success"] = false,
                    ["message"] = "not supported by VSL",
                };
                _ = _framing.WriteAsync(reply.ToJsonString());
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoop is not null)
            await Task.WhenAny(_readLoop, Task.Delay(1000)).ConfigureAwait(false);
        _cts.Dispose();
    }
}
