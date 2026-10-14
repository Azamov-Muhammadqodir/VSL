using System.Collections.Concurrent;
using System.Text;
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

    readonly Stream _input;
    readonly Stream _output;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly ConcurrentDictionary<string, Func<JsonElement, Task<object?>>> _requestHandlers = new();
    readonly ConcurrentDictionary<string, Action<JsonElement>> _notificationHandlers = new();
    readonly CancellationTokenSource _cts = new();
    long _nextId;
    Task? _readLoop;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

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

    async Task WriteAsync(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header).ConfigureAwait(false);
            await _output.WriteAsync(body).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var length = await ReadHeadersAsync().ConfigureAwait(false);
                if (length < 0)
                    break;
                var buffer = new byte[length];
                await _input.ReadExactlyAsync(buffer, _cts.Token).ConfigureAwait(false);
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

    /// <summary>Reads header lines; returns Content-Length, or -1 at end of stream.</summary>
    async Task<int> ReadHeadersAsync()
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(one, _cts.Token).ConfigureAwait(false);
            if (n == 0)
                return -1;
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0)
                        return length;
                    continue;
                }
                var colon = text.IndexOf(':');
                if (colon > 0 && text[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(text[(colon + 1)..].Trim());
            }
            else
            {
                line.Append((char)one[0]);
            }
        }
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
