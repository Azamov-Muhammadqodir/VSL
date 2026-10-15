using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VSL.Claude;

public sealed record ClaudeStartOptions(
    string Executable,
    string WorkingDirectory,
    string? Model = null,
    string? PermissionMode = null,
    string? Resume = null,
    ClaudeAccount? Account = null);

/// <summary>
/// One running Claude Code CLI in headless streaming mode (the interface the Agent SDK uses): JSON lines on
/// stdin/stdout, prompts as user messages, permission prompts as can_use_tool control requests answered by VSL,
/// and control requests (initialize, interrupt, set_permission_mode, set_model) with matching responses.
/// </summary>
public sealed class ClaudeSession : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    readonly Process _process;
    readonly StreamWriter _stdin;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly StringBuilder _stderr = new();
    readonly Task _reader;
    readonly TaskCompletionSource<bool> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<bool> _stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _requestCounter;
    bool _disposed;

    ClaudeSession(Process process)
    {
        _process = process;
        // UTF-8 whatever the console code page is (Windows), as JSON lines.
        _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
        // The exit is awaited through Exited, not Process.WaitForExit: on .NET Framework that throws (NullReference in
        // AsyncStreamReader) while stderr is read asynchronously, which brought Visual Studio down.
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _exited.TrySetResult(true);
        if (_process.HasExited)
            _exited.TrySetResult(true);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                _stderrDone.TrySetResult(true);
                return;
            }
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8000)
                    _stderr.Remove(0, _stderr.Length - 8000);
            }
        };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised on a background thread for everything the CLI reports.</summary>
    public event Action<ClaudeEvent>? Event;

    /// <summary>The initialize response: slash commands, models, account, current permission mode.</summary>
    public JsonElement Initialization { get; private set; }

    public string? SessionId { get; private set; }

    public bool HasExited => _process.HasExited;

    /// <summary>The command-line arguments for <paramref name="options"/> (the SDK's streaming invocation).</summary>
    public static List<string> Arguments(ClaudeStartOptions options)
    {
        List<string> args =
        [
            "--output-format", "stream-json", "--verbose", "--input-format", "stream-json", "--include-partial-messages",
            // Permission prompts come back to VSL as can_use_tool control requests instead of being denied.
            "--permission-prompt-tool", "stdio",
        ];
        if (options.PermissionMode is { Length: > 0 } mode && mode != "default")
            args.AddRange(["--permission-mode", mode]);
        if (options.Model is { Length: > 0 } model && model != "default")
            args.AddRange(["--model", model]);
        if (options.Resume is { Length: > 0 } resume)
            args.AddRange(["--resume", resume]);
        return args;
    }

    public static async Task<ClaudeSession> StartAsync(ClaudeStartOptions options, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(options.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // No console window on Windows (Visual Studio is a GUI process).
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in Arguments(options))
            psi.ArgumentList.Add(arg);
        // A CLI started from inside another Claude Code session must not think it is nested.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment["PWD"] = options.WorkingDirectory;
        options.Account?.Apply(psi.Environment);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Claude Code could not be started.");
        var session = new ClaudeSession(process) { SessionId = options.Resume };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            session.Initialization = await session.RequestAsync(new { subtype = "initialize", hooks = (object?)null }, timeout.Token);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            // A CLI that quits at once (not signed in, no access...) says why on stderr; "the pipe has been ended" does not.
            if (session._exited.Task.IsCompleted && session.StderrTail(6) is { Length: > 0 } reason)
                throw new InvalidOperationException(reason, ex);
            throw;
        }
        return session;
    }

    /// <summary>
    /// Sends a prompt: images first (as the Messages API recommends), then the text, then extra text blocks such as
    /// the editor context.
    /// </summary>
    public Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null)
    {
        var content = new List<object>();
        foreach (var image in images ?? [])
            content.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = image.Base64 } });
        if (text.Length > 0)
            content.Add(new { type = "text", text });
        foreach (var block in extraBlocks ?? [])
            content.Add(new { type = "text", text = block });
        return WriteAsync(new
        {
            type = "user",
            message = new { role = "user", content },
            parent_tool_use_id = (string?)null,
            session_id = SessionId ?? "default",
        });
    }

    /// <summary>
    /// Runs a slash command (/context, /effort high, /model sonnet...). The CLI only treats a message as a
    /// command when its content is the plain command string, so nothing else may go with it.
    /// </summary>
    public Task SendCommandAsync(string command) =>
        WriteAsync(new
        {
            type = "user",
            message = new { role = "user", content = command },
            parent_tool_use_id = (string?)null,
            session_id = SessionId ?? "default",
        });

    /// <summary>Answers a can_use_tool request: allow (optionally with changed input / remembered rules) or deny.</summary>
    public Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision) =>
        WriteAsync(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = decision.ToResponse() },
        });

    public Task InterruptAsync() => RequestAsync(new { subtype = "interrupt" });

    public Task SetPermissionModeAsync(string mode) => RequestAsync(new { subtype = "set_permission_mode", mode });

    public Task SetModelAsync(string? model) => RequestAsync(new { subtype = "set_model", model = model is null or "default" ? null : model });

    /// <summary>Plan limits (session / weekly windows), subscription and what drives usage.</summary>
    public Task<JsonElement> GetUsageAsync() => RequestAsync(new { subtype = "get_usage" });

    /// <summary>Effective settings and what is applied (model, effort...).</summary>
    public Task<JsonElement> GetSettingsAsync() => RequestAsync(new { subtype = "get_settings" });

    /// <summary>Applies settings for this session only, as command-line flags would (e.g. effortLevel).</summary>
    public Task ApplyFlagSettingsAsync(object settings) => RequestAsync(new { subtype = "apply_flag_settings", settings });

    public Task<JsonElement> McpStatusAsync() => RequestAsync(new { subtype = "mcp_status" });

    public Task McpReconnectAsync(string serverName) => RequestAsync(new { subtype = "mcp_reconnect", serverName });

    public Task McpToggleAsync(string serverName, bool enabled) => RequestAsync(new { subtype = "mcp_toggle", serverName, enabled });

    /// <summary>Starts a server's OAuth sign-in; the result carries the URL to open in the browser.</summary>
    public Task<JsonElement> McpAuthenticateAsync(string serverName) => RequestAsync(new { subtype = "mcp_authenticate", serverName });

    async Task<JsonElement> RequestAsync(object request, CancellationToken ct = default)
    {
        var id = $"req_{Interlocked.Increment(ref _requestCounter)}_{Random.Shared.Next():x8}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var registration = ct.Register(() => tcs.TrySetCanceled());
        await WriteAsync(new { type = "control_request", request_id = id, request });
        return await tcs.Task;
    }

    async Task WriteAsync(object message)
    {
        var line = JsonSerializer.Serialize(message, Json);
        await _writeLock.WaitAsync();
        try
        {
            if (_process.HasExited)
                throw new InvalidOperationException("Claude Code is not running.");
            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith('{'))
                    continue;
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (doc)
                    Dispatch(doc.RootElement);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        await WaitForExitAsync(CancellationToken.None);
        foreach (var pending in _pending.Values)
            pending.TrySetException(new InvalidOperationException("Claude Code exited."));
        string stderr;
        lock (_stderr)
            stderr = _stderr.ToString();
        if (!_disposed)
            Event?.Invoke(new ClaudeExited(_process.ExitCode, stderr));
    }

    void Dispatch(JsonElement root)
    {
        if (ClaudeProtocol.Str(root, "type") == "control_response" && root.TryGetProperty("response", out var response))
        {
            if (ClaudeProtocol.Str(response, "request_id") is { } id && _pending.TryRemove(id, out var tcs))
            {
                if (ClaudeProtocol.Str(response, "subtype") == "error")
                    tcs.TrySetException(new InvalidOperationException(ClaudeProtocol.Str(response, "error") ?? "Request failed."));
                else
                    tcs.TrySetResult(response.TryGetProperty("response", out var body) ? body.Clone() : default);
            }
            return;
        }
        foreach (var ev in ClaudeProtocol.Parse(root))
        {
            if (ev is ClaudeInit init)
                SessionId = init.SessionId;
            Event?.Invoke(ev);
        }
    }

    /// <summary>The last lines the CLI wrote to stderr.</summary>
    internal string StderrTail(int lines)
    {
        lock (_stderr)
            return string.Join("\n", _stderr.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Reverse().Take(lines).Reverse());
    }

    /// <summary>Until the CLI exits (and its stderr is read to the end, briefly), or <paramref name="ct"/> is cancelled.</summary>
    async Task WaitForExitAsync(CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetCanceled()))
            await await Task.WhenAny(_exited.Task, cancelled.Task);
        await Task.WhenAny(_stderrDone.Task, Task.Delay(1000));
    }

    /// <summary>Ends the CLI: closes its input, gives it a moment, then kills it. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
                _stdin.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitForExitAsync(wait.Token);
        }
        catch (Exception)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone, or not ours to stop.
            }
        }
        try
        {
            await Task.WhenAny(_reader, Task.Delay(2000));
            _process.Dispose();
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>How VSL answers a permission prompt.</summary>
public sealed record ClaudePermissionDecision(
    bool Allow,
    JsonElement? UpdatedInput = null,
    IReadOnlyList<JsonElement>? UpdatedPermissions = null,
    string? Message = null,
    bool Interrupt = false)
{
    public static ClaudePermissionDecision AllowOnce(JsonElement input) => new(true, input);

    public static ClaudePermissionDecision Deny(string message) => new(false, Message: message);

    internal object ToResponse() => Allow
        ? (object)new { behavior = "allow", updatedInput = UpdatedInput, updatedPermissions = UpdatedPermissions is { Count: > 0 } p ? p : null }
        : new { behavior = "deny", message = Message ?? "The user declined.", interrupt = Interrupt ? true : (bool?)null };
}
