using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VSL.Lsp;

public sealed class LspException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>JSON-RPC 2.0 over the LSP "Content-Length" framed transport.</summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly MessageFraming _framing;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly ConcurrentDictionary<string, Func<JsonElement, Task<object?>>> _requestHandlers = new();
    readonly ConcurrentDictionary<string, Action<JsonElement>> _notificationHandlers = new();
    readonly CancellationTokenSource _cts = new();
    long _nextId;
    Task? _readLoop;

    public JsonRpcConnection(Stream input, Stream output) => _framing = new MessageFraming(input, output);

    /// <summary>Raised once when the stream closes or a framing error occurs.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>Raised for server-to-client requests and notifications that have no handler.</summary>
    public event Action<string>? Unhandled;

    public void OnRequest(string method, Func<JsonElement, Task<object?>> handler) => _requestHandlers[method] = handler;

    public void OnNotification(string method, Action<JsonElement> handler) => _notificationHandlers[method] = handler;

    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
            {
                t.TrySetCanceled(ct);
                _ = NotifyAsync("$/cancelRequest", new { id });
            }
        });
        await WriteAsync(Envelope(method, @params, id)).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task NotifyAsync(string method, object? @params) => WriteAsync(Envelope(method, @params, null));

    static JsonObject Envelope(string method, object? @params, long? id)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0" };
        if (id is { } value)
            message["id"] = value;
        message["method"] = method;
        if (@params is not null)
            message["params"] = ToNode(@params);
        return message;
    }

    static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n,
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), JsonOptions),
    };

    Task WriteAsync(JsonObject message) => _framing.WriteAsync(message.ToJsonString(JsonOptions));

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

        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new IOException("Language server connection closed.", error));
        _pending.Clear();
        Disconnected?.Invoke(error);
    }

    void Dispatch(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var idElement);
        if (message.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString()!;
            var @params = message.TryGetProperty("params", out var p) ? p : default;
            if (hasId)
                _ = HandleServerRequestAsync(idElement.Clone(), method, @params);
            else if (_notificationHandlers.TryGetValue(method, out var handler))
                SafeInvoke(() => handler(@params));
            else
                Unhandled?.Invoke(method);
            return;
        }

        if (!hasId || idElement.ValueKind != JsonValueKind.Number || !_pending.TryRemove(idElement.GetInt64(), out var tcs))
            return;
        if (message.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            tcs.TrySetException(new LspException(code, msg));
        }
        else
        {
            tcs.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
        }
    }

    async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement @params)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (_requestHandlers.TryGetValue(method, out var handler))
        {
            try
            {
                response["result"] = ToNode(await handler(@params).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                response["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
            }
        }
        else
        {
            // Answering null keeps servers happy for optional client features we do not implement.
            Unhandled?.Invoke(method);
            response["result"] = null;
        }
        await WriteAsync(response).ConfigureAwait(false);
    }

    static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A faulty notification handler must not kill the read loop.
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
