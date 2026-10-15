using System.Diagnostics;
using System.Text.Json;
using VSL.Build;

namespace VSL.Lsp;

/// <summary>
/// Client for Microsoft's Roslyn language server (the engine behind C# IntelliSense in Visual Studio and VS Code),
/// including cohosted Razor support.
/// </summary>
public sealed class RoslynClient : IAsyncDisposable
{
    readonly Process _process;
    readonly JsonRpcConnection _rpc;
    readonly FileWatchBridge _watch;
    readonly HtmlLanguageService _html = new();
    int _exited;

    RoslynClient(Process process, JsonRpcConnection rpc, string solutionDir)
    {
        _process = process;
        _rpc = rpc;
        _watch = new FileWatchBridge(solutionDir, changes => _rpc.NotifyAsync("workspace/didChangeWatchedFiles", new
        {
            changes = changes.Select(c => new { uri = c.Uri, type = c.Type }).ToArray(),
        }));
    }

    public event Action<string>? Log;
    public event Action? ProjectsLoaded;
    public event Action? DiagnosticsRefreshRequested;
    public event Action<IReadOnlyList<string>>? RestoreRequested;

    /// <summary>Progress text from the server (e.g. project loading); null when the operation ends.</summary>
    public event Action<string?>? Progress;

    public event Action<Exception?>? Exited;

    public bool IsReady { get; private set; }

    public int WatcherCount => _watch.WatcherCount;

    public static string LanguageIdFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" or ".cshtml" => "razor",
        _ => "plaintext",
    };

    public static bool IsSupported(string path) => LanguageIdFor(path) != "plaintext";

    static string ToUri(string path) => new Uri(path).AbsoluteUri;

    public static async Task<RoslynClient> StartAsync(RoslynServerInstall install, string solutionPath, string logDir, CancellationToken ct)
    {
        Directory.CreateDirectory(logDir);
        var psi = new ProcessStartInfo(install.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
        };
        foreach (var a in new[]
                 {
                     "--stdio", "--logLevel", "Information", "--extensionLogDirectory", logDir,
                     "--csharpDesignTimePath", install.CSharpDesignTimeTargets,
                     $"--clientProcessId={Environment.ProcessId}",
                 })
            psi.ArgumentList.Add(a);
        DotnetCli.ApplyEnvironment(psi);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start roslyn-language-server.");
        var rpc = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var client = new RoslynClient(process, rpc, Path.GetDirectoryName(solutionPath)!);
        client.Wire();
        rpc.Start();
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                client.Log?.Invoke("[stderr] " + line);
        });

        var root = Path.GetDirectoryName(solutionPath)!;
        await rpc.RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "VSL", version = "0.1" },
            rootUri = ToUri(root),
            workspaceFolders = new[] { new { uri = ToUri(root), name = Path.GetFileName(root) } },
            capabilities = ClientCapabilities,
        }, ct);
        await rpc.NotifyAsync("initialized", new { });
        if (Workspace.SolutionLoader.IsProjectFile(solutionPath))
            await rpc.NotifyAsync("project/open", new { projects = new[] { ToUri(solutionPath) } });
        else
            await rpc.NotifyAsync("solution/open", new { solution = ToUri(solutionPath) });
        return client;
    }

    static readonly object ClientCapabilities = new
    {
        workspace = new
        {
            // Lets the server delegate file watching to us (see FileWatchBridge).
            didChangeWatchedFiles = new { dynamicRegistration = true, relativePatternSupport = true },
            configuration = true,
            workspaceFolders = true,
            applyEdit = true,
            workspaceEdit = new { documentChanges = true },
            diagnostics = new { refreshSupport = true },
        },
        textDocument = new
        {
            synchronization = new { didSave = true },
            completion = new
            {
                contextSupport = true,
                completionItem = new
                {
                    snippetSupport = true,
                    labelDetailsSupport = true,
                    resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits", "textEdit" } },
                },
                completionList = new { itemDefaults = new[] { "editRange", "insertTextFormat", "data", "commitCharacters" } },
            },
            hover = new { contentFormat = new[] { "markdown", "plaintext" } },
            signatureHelp = new
            {
                signatureInformation = new
                {
                    documentationFormat = new[] { "plaintext", "markdown" },
                    parameterInformation = new { labelOffsetSupport = true },
                },
            },
            publishDiagnostics = new { },
            diagnostic = new { dynamicRegistration = true },
            definition = new { },
            references = new { },
            implementation = new { },
            formatting = new { },
            foldingRange = new { lineFoldingOnly = false, foldingRange = new { collapsedText = true } },
            rename = new { prepareSupport = false },
            codeAction = new
            {
                dataSupport = true,
                isPreferredSupport = true,
                resolveSupport = new { properties = new[] { "edit" } },
                codeActionLiteralSupport = new
                {
                    codeActionKind = new
                    {
                        valueSet = new[] { "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports" },
                    },
                },
            },
        },
        window = new { workDoneProgress = true },
    };

    void Wire()
    {
        _rpc.OnRequest("workspace/configuration", p =>
            Task.FromResult<object?>(Enumerable.Repeat<object?>(null, p.GetProperty("items").GetArrayLength()).ToArray()));
        _rpc.OnRequest("client/registerCapability", p =>
        {
            foreach (var reg in p.GetProperty("registrations").EnumerateArray())
                if (reg.GetProperty("method").GetString() == "workspace/didChangeWatchedFiles")
                    _watch.Register(reg.GetProperty("id").GetString()!, reg.GetProperty("registerOptions"));
            return Task.FromResult<object?>(null);
        });
        _rpc.OnRequest("client/unregisterCapability", p =>
        {
            // The spec really spells it "unregisterations".
            if (p.TryGetProperty("unregisterations", out var list))
                foreach (var u in list.EnumerateArray())
                    _watch.Unregister(u.GetProperty("id").GetString()!);
            return Task.FromResult<object?>(null);
        });
        _rpc.OnRequest("window/workDoneProgress/create", _ => Task.FromResult<object?>(null));
        _rpc.OnRequest("workspace/diagnostic/refresh", _ =>
        {
            DiagnosticsRefreshRequested?.Invoke();
            return Task.FromResult<object?>(null);
        });
        // Razor cohosting: the HTML half of .razor files is served by the client.
        _rpc.OnRequest("razor/updateHtml", p =>
        {
            _html.Update(p);
            return Task.FromResult<object?>(null);
        });
        _rpc.OnNotification("razor/updateHtml", _html.Update);
        foreach (var method in new[]
                 {
                     "textDocument/completion", "textDocument/hover", "textDocument/formatting", "textDocument/rangeFormatting",
                     "textDocument/onTypeFormatting", "textDocument/foldingRange", "textDocument/documentHighlight",
                     "textDocument/documentSymbol", "textDocument/documentColor", "textDocument/codeAction",
                     "textDocument/inlayHint", "textDocument/diagnostic", "textDocument/signatureHelp",
                 })
            _rpc.OnRequest(method, p => Task.FromResult(_html.Handle(method, p)));
        _rpc.OnRequest("workspace/_roslyn_projectNeedsRestore", p =>
        {
            var paths = p.TryGetProperty("projectFilePaths", out var arr)
                ? arr.EnumerateArray().Select(x => x.GetString()!).ToList()
                : [];
            RestoreRequested?.Invoke(paths);
            return Task.FromResult<object?>(null);
        });

        _rpc.OnNotification("window/logMessage", p =>
        {
            var type = p.GetProperty("type").GetInt32();
            if (type <= 3)
                Log?.Invoke(p.GetProperty("message").GetString() ?? "");
        });
        _rpc.OnNotification("window/_roslyn_showToast", p =>
            Log?.Invoke("[toast] " + (p.TryGetProperty("message", out var m) ? m.GetString() : p.GetRawText())));
        _rpc.OnNotification("workspace/projectInitializationComplete", _ =>
        {
            IsReady = true;
            ProjectsLoaded?.Invoke();
        });
        _rpc.OnNotification("$/progress", p =>
        {
            if (!p.TryGetProperty("value", out var v) || !v.TryGetProperty("kind", out var kind))
                return;
            if (kind.GetString() == "end")
            {
                Progress?.Invoke(null);
                return;
            }
            var title = v.TryGetProperty("title", out var t) ? t.GetString() : null;
            var message = v.TryGetProperty("message", out var m) ? m.GetString() : null;
            Progress?.Invoke(string.Join(": ", new[] { title, message }.Where(s => !string.IsNullOrEmpty(s))));
        });
        _rpc.Unhandled += method => Log?.Invoke($"[client] no handler for '{method}'");
        _rpc.Disconnected += ex =>
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
                Exited?.Invoke(ex);
        };
    }

    // ---- Document sync ---------------------------------------------------------------------------------------

    public Task DidOpenAsync(string path, string text) => _rpc.NotifyAsync("textDocument/didOpen", new
    {
        textDocument = new { uri = ToUri(path), languageId = LanguageIdFor(path), version = 1, text },
    });

    public Task DidChangeAsync(string path, int version, IReadOnlyList<LspContentChange> changes) => _rpc.NotifyAsync("textDocument/didChange", new
    {
        textDocument = new { uri = ToUri(path), version },
        contentChanges = changes.Select(c => c.Range is { } r ? (object)new { range = r, text = c.Text } : new { text = c.Text }).ToArray(),
    });

    public Task DidSaveAsync(string path) => _rpc.NotifyAsync("textDocument/didSave", new { textDocument = new { uri = ToUri(path) } });

    public Task DidCloseAsync(string path) => _rpc.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = ToUri(path) } });

    // ---- Language features -----------------------------------------------------------------------------------

    static object DocPos(string path, LspPosition pos) => new { textDocument = new { uri = ToUri(path) }, position = pos };

    public async Task<LspCompletionList> CompletionAsync(string path, LspPosition pos, string? triggerCharacter, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            context = triggerCharacter is null ? (object)new { triggerKind = 1 } : new { triggerKind = 2, triggerCharacter },
        }, ct);
        return LspParse.Completion(result);
    }

    public async Task<(LspCompletionItem Item, string? Documentation)> ResolveCompletionAsync(LspCompletionItem item, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("completionItem/resolve", item.Raw, ct);
        if (result.ValueKind != JsonValueKind.Object)
            return (item, null);
        var resolved = LspParse.CompletionItem(result, default);
        // Resolve may drop the itemDefaults-derived edit; keep the original one in that case.
        if (resolved.TextEdit is null && item.TextEdit is not null)
            resolved = resolved with { TextEdit = item.TextEdit };
        var doc = result.TryGetProperty("documentation", out var d) ? LspParse.Markup(d) : null;
        return (resolved, doc);
    }

    public async Task<string?> HoverAsync(string path, LspPosition pos, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("textDocument/hover", DocPos(path, pos), ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contents", out var c) ? LspParse.Markup(c) : null;
    }

    public async Task<LspSignatureHelp?> SignatureHelpAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.SignatureHelp(await _rpc.RequestAsync("textDocument/signatureHelp", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await _rpc.RequestAsync("textDocument/definition", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> ImplementationAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await _rpc.RequestAsync("textDocument/implementation", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await _rpc.RequestAsync("textDocument/references", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            context = new { includeDeclaration = true },
        }, ct));

    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(string path, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri = ToUri(path) } }, ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items)
            ? LspParse.Diagnostics(items)
            : [];
    }

    public async Task<IReadOnlyList<LspFoldingRange>> FoldingRangesAsync(string path, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("textDocument/foldingRange", new { textDocument = new { uri = ToUri(path) } }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];
        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return result.EnumerateArray()
            .Select(r => new LspFoldingRange(Int(r, "startLine") ?? 0, Int(r, "startCharacter"), Int(r, "endLine") ?? 0, Int(r, "endCharacter"),
                Str(r, "kind"), Str(r, "collapsedText")))
            .ToList();
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormatAsync(string path, int tabSize, bool insertSpaces, CancellationToken ct) =>
        LspParse.TextEdits(await _rpc.RequestAsync("textDocument/formatting", new
        {
            textDocument = new { uri = ToUri(path) },
            options = new { tabSize, insertSpaces },
        }, ct));

    /// <summary>Returns per-file edits for a rename, or null when the symbol cannot be renamed.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>?> RenameAsync(string path, LspPosition pos, string newName, CancellationToken ct)
    {
        var result = await _rpc.RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            newName,
        }, ct);
        if (result.ValueKind != JsonValueKind.Object)
            return null;
        var edits = new Dictionary<string, IReadOnlyList<LspTextEdit>>();
        if (result.TryGetProperty("documentChanges", out var dc))
        {
            foreach (var change in dc.EnumerateArray())
                if (change.TryGetProperty("textDocument", out var td))
                    edits[new Uri(td.GetProperty("uri").GetString()!).LocalPath] = LspParse.TextEdits(change.GetProperty("edits"));
        }
        else if (result.TryGetProperty("changes", out var changes))
        {
            foreach (var file in changes.EnumerateObject())
                edits[new Uri(file.Name).LocalPath] = LspParse.TextEdits(file.Value);
        }
        return edits;
    }

    /// <summary>Quick Actions and Refactorings (Ctrl+.) for a range, given the diagnostics it overlaps.</summary>
    public async Task<IReadOnlyList<LspCodeAction>> CodeActionsAsync(string path, LspRange range, IReadOnlyList<LspDiagnostic> diagnostics, CancellationToken ct) =>
        LspParse.CodeActions(await _rpc.RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri = ToUri(path) },
            range,
            context = new
            {
                // The server matches fixes to diagnostics by their original JSON, so send them back verbatim.
                diagnostics = diagnostics.Where(d => d.Raw.ValueKind == JsonValueKind.Object).Select(d => d.Raw).ToArray(),
                triggerKind = 1,
            },
        }, ct));

    /// <summary>Computes the edit for a code action (codeAction/resolve unless the server already sent it).</summary>
    public async Task<IReadOnlyList<LspFileOperation>> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct)
    {
        if (action.Raw.TryGetProperty("edit", out var inline))
            return LspParse.WorkspaceEdit(inline);
        var resolved = await _rpc.RequestAsync("codeAction/resolve", action.Raw, ct);
        return resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty("edit", out var edit)
            ? LspParse.WorkspaceEdit(edit)
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        _watch.Dispose();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _rpc.RequestAsync("shutdown", null, cts.Token);
            await _rpc.NotifyAsync("exit", null);
        }
        catch
        {
            // Server already gone or unresponsive; fall through to kill.
        }
        await _rpc.DisposeAsync();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}
