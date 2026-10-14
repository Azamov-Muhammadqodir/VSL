using System.Diagnostics;
using System.Text.Json;
using VSL.Build;

namespace VSL.Debugging;

public sealed record StackFrameInfo(int Id, string Name, string? SourcePath, int Line, int Column);

public sealed record ScopeInfo(string Name, int VariablesReference, bool Expensive);

public sealed record VariableInfo(string Name, string Value, string? Type, int VariablesReference);

public sealed record BreakpointResult(bool Verified, int? Line, string? Message);

public sealed record EvaluateResult(string Value, string? Type, int VariablesReference);

public sealed record LaunchRequest(
    string Program,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>One debugging session against netcoredbg (launch, breakpoints, stepping, inspection).</summary>
public sealed class DebugSession : IAsyncDisposable
{
    readonly Process _adapter;
    readonly DapConnection _dap;
    int _ended;

    DebugSession(Process adapter, DapConnection dap)
    {
        _adapter = adapter;
        _dap = dap;
        dap.EventReceived += OnEvent;
        dap.Disconnected += _ => End(null);
    }

    /// <summary>The debuggee stopped (breakpoint, step, exception, pause): thread id, reason, description.</summary>
    public event Action<int, string, string?>? Stopped;

    public event Action? Continued;

    /// <summary>Program or adapter output: category ("stdout", "stderr", "console"), text.</summary>
    public event Action<string, string>? Output;

    /// <summary>The session is over; carries the exit code when the adapter reported one.</summary>
    public event Action<int?>? Ended;

    /// <summary>A breakpoint became bound (or unbound) later, e.g. when its module loaded: file, requested line, verified.</summary>
    public event Action<string, int, bool>? BreakpointChanged;

    readonly Dictionary<int, (string File, int Line)> _breakpointIds = [];

    int? _exitCode;

    public static async Task<DebugSession> LaunchAsync(
        string adapterPath,
        LaunchRequest launch,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(adapterPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--interpreter=vscode");
        DotnetCli.ApplyEnvironment(psi);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start netcoredbg.");
        var dap = new DapConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var session = new DebugSession(process, dap);
        beforeConfigurationDone?.Invoke(session);
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                session.Output?.Invoke("console", line + "\n");
        });

        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dap.EventReceived += (name, _) =>
        {
            if (name == "initialized")
                initialized.TrySetResult();
        };
        dap.Start();

        try
        {
            await dap.RequestAsync("initialize", new
            {
                clientID = "vsl",
                clientName = "VSL",
                adapterID = "coreclr",
                pathFormat = "path",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                supportsVariableType = true,
                supportsRunInTerminalRequest = false,
                locale = "en-us",
            }, ct);

            // Some adapters answer "launch" only after configurationDone, so do not await it yet.
            var launchTask = dap.RequestAsync("launch", new
            {
                name = "VSL Launch",
                type = "coreclr",
                request = "launch",
                program = launch.Program,
                args = launch.Args,
                cwd = launch.WorkingDirectory,
                env = launch.Environment,
                stopAtEntry = false,
                justMyCode = true,
                enableStepFiltering = true,
                console = "internalConsole",
            }, ct);
            await Task.WhenAny(initialized.Task, launchTask).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (launchTask.IsFaulted)
                await launchTask;
            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            foreach (var (file, lines) in breakpoints)
                onBreakpointsVerified(file, await session.SetBreakpointsAsync(file, lines, ct));
            await dap.RequestAsync("configurationDone", null, ct);
            await launchTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    void OnEvent(string name, JsonElement body)
    {
        switch (name)
        {
            case "stopped":
                var thread = body.TryGetProperty("threadId", out var t) ? t.GetInt32() : 0;
                var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                var text = body.TryGetProperty("text", out var tx) ? tx.GetString()
                    : body.TryGetProperty("description", out var d) ? d.GetString() : null;
                Stopped?.Invoke(thread, reason, text);
                break;
            case "continued":
                Continued?.Invoke();
                break;
            case "output":
                var category = body.TryGetProperty("category", out var c) ? c.GetString() ?? "console" : "console";
                if (body.TryGetProperty("output", out var o))
                    Output?.Invoke(category, o.GetString() ?? "");
                break;
            case "exited":
                if (body.TryGetProperty("exitCode", out var code))
                    _exitCode = code.GetInt32();
                break;
            case "terminated":
                End(_exitCode);
                break;
            case "breakpoint":
                if (body.TryGetProperty("breakpoint", out var bp) && bp.TryGetProperty("id", out var id))
                {
                    (string File, int Line) location;
                    lock (_breakpointIds)
                        if (!_breakpointIds.TryGetValue(id.GetInt32(), out location))
                            break;
                    BreakpointChanged?.Invoke(location.File, location.Line, bp.TryGetProperty("verified", out var ok) && ok.GetBoolean());
                }
                break;
        }
    }

    void End(int? exitCode)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            Ended?.Invoke(exitCode ?? _exitCode);
    }

    public bool HasEnded => _ended == 1;

    public async Task<IReadOnlyList<BreakpointResult>> SetBreakpointsAsync(string file, IReadOnlyList<int> lines, CancellationToken ct = default)
    {
        var body = await _dap.RequestAsync("setBreakpoints", new
        {
            source = new { path = file, name = Path.GetFileName(file) },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
            lines,
            sourceModified = false,
        }, ct);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("breakpoints", out var list))
            return lines.Select(_ => new BreakpointResult(false, null, null)).ToList();
        lock (_breakpointIds)
        {
            foreach (var key in _breakpointIds.Where(kv => kv.Value.File == file).Select(kv => kv.Key).ToList())
                _breakpointIds.Remove(key);
            var i = 0;
            foreach (var b in list.EnumerateArray())
            {
                if (b.TryGetProperty("id", out var id) && i < lines.Count)
                    _breakpointIds[id.GetInt32()] = (file, lines[i]);
                i++;
            }
        }
        return list.EnumerateArray().Select(b => new BreakpointResult(
            b.TryGetProperty("verified", out var v) && v.GetBoolean(),
            b.TryGetProperty("line", out var l) ? l.GetInt32() : null,
            b.TryGetProperty("message", out var m) ? m.GetString() : null)).ToList();
    }

    public Task ContinueAsync(int threadId) => _dap.RequestAsync("continue", new { threadId });

    public Task StepOverAsync(int threadId) => _dap.RequestAsync("next", new { threadId });

    public Task StepInAsync(int threadId) => _dap.RequestAsync("stepIn", new { threadId });

    public Task StepOutAsync(int threadId) => _dap.RequestAsync("stepOut", new { threadId });

    public async Task PauseAsync()
    {
        var threads = await _dap.RequestAsync("threads", null);
        var id = threads.TryGetProperty("threads", out var list) && list.GetArrayLength() > 0 ? list[0].GetProperty("id").GetInt32() : 0;
        await _dap.RequestAsync("pause", new { threadId = id });
    }

    public async Task<IReadOnlyList<StackFrameInfo>> StackTraceAsync(int threadId, CancellationToken ct = default)
    {
        var body = await _dap.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 100 }, ct);
        if (!body.TryGetProperty("stackFrames", out var frames))
            return [];
        return frames.EnumerateArray().Select(f =>
        {
            string? path = null;
            if (f.TryGetProperty("source", out var s) && s.TryGetProperty("path", out var p))
                path = p.GetString();
            return new StackFrameInfo(f.GetProperty("id").GetInt32(), f.GetProperty("name").GetString() ?? "",
                path, f.TryGetProperty("line", out var l) ? l.GetInt32() : 0, f.TryGetProperty("column", out var c) ? c.GetInt32() : 0);
        }).ToList();
    }

    public async Task<IReadOnlyList<ScopeInfo>> ScopesAsync(int frameId, CancellationToken ct = default)
    {
        var body = await _dap.RequestAsync("scopes", new { frameId }, ct);
        if (!body.TryGetProperty("scopes", out var scopes))
            return [];
        return scopes.EnumerateArray().Select(s => new ScopeInfo(
            s.GetProperty("name").GetString() ?? "",
            s.GetProperty("variablesReference").GetInt32(),
            s.TryGetProperty("expensive", out var e) && e.GetBoolean())).ToList();
    }

    public async Task<IReadOnlyList<VariableInfo>> VariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        var body = await _dap.RequestAsync("variables", new { variablesReference }, ct);
        if (!body.TryGetProperty("variables", out var vars))
            return [];
        return vars.EnumerateArray().Select(v => new VariableInfo(
            v.GetProperty("name").GetString() ?? "",
            v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "",
            v.TryGetProperty("type", out var t) ? t.GetString() : null,
            v.TryGetProperty("variablesReference", out var r) ? r.GetInt32() : 0)).ToList();
    }

    /// <summary>Evaluates an expression in a frame; context is "watch", "hover" or "repl".</summary>
    public async Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string context, CancellationToken ct = default)
    {
        var body = await _dap.RequestAsync("evaluate", new { expression, frameId, context }, ct);
        return new EvaluateResult(
            body.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            body.TryGetProperty("type", out var t) ? t.GetString() : null,
            body.TryGetProperty("variablesReference", out var v) ? v.GetInt32() : 0);
    }

    public async Task TerminateAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _dap.RequestAsync("disconnect", new { terminateDebuggee = true }, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or DapException)
        {
        }
        End(_exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (!HasEnded && !_adapter.HasExited)
            await TerminateAsync();
        await _dap.DisposeAsync();
        try
        {
            if (!_adapter.HasExited)
                _adapter.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _adapter.Dispose();
    }
}
