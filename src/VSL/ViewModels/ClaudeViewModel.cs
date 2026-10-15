using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;
using VSL.Services;

namespace VSL.ViewModels;

/// <summary>
/// The Claude Code tool window: a conversation with the Claude Code CLI running in the solution folder, with its
/// tool calls, inline diffs, permission prompts, clarifying questions and plans, like the VS Code extension.
/// </summary>
public sealed partial class ClaudeViewModel : ObservableObject, IAsyncDisposable
{
    static readonly string[] FileChangingTools = ["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash"];

    static readonly ClaudeModelChoice[] DefaultModels =
    [
        new("default", "Default (recommended)", null),
        new("opus", "Opus", null),
        new("sonnet", "Sonnet", null),
        new("haiku", "Haiku", null),
    ];

    readonly Settings _settings;
    readonly Func<string> _workingDirectory;
    readonly Func<ClaudeEditorContext?> _editorContext;
    readonly Func<IEnumerable<string>> _projectFiles;
    readonly Func<string?> _findCli;
    readonly Dictionary<string, ToolChatItem> _tools = [];
    readonly DispatcherTimer _timer;
    ClaudeSession? _session;
    Task<ClaudeSession>? _starting;
    string? _sessionCwd;
    string? _resumeId;
    DateTime _turnStarted;
    int _tokens;
    bool _thinking;
    bool _changingModeFromCli;

    public ClaudeViewModel(Settings settings, Func<string> workingDirectory, Func<ClaudeEditorContext?> editorContext,
        Func<IEnumerable<string>> projectFiles, Func<string?>? findCli = null)
    {
        _settings = settings;
        _workingDirectory = workingDirectory;
        _editorContext = editorContext;
        _projectFiles = projectFiles;
        _findCli = findCli ?? ClaudeCli.FindExecutable;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateStatus();
        foreach (var model in DefaultModels)
            Models.Add(model);
        SelectedModel = Models.FirstOrDefault(m => m.Value == settings.ClaudeModel) ?? Models[0];
        PermissionMode = settings.ClaudePermissionMode is "acceptEdits" or "plan" ? settings.ClaudePermissionMode : "default";
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        CheckCli();
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    public ObservableCollection<ClaudeConversation> History { get; } = [];

    public ObservableCollection<ClaudeModelChoice> Models { get; } = [];

    public ObservableCollection<ClaudeCommand> Commands { get; } = [];

    public ObservableCollection<ClaudeSuggestion> Suggestions { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Open a file (and line) in the editor.</summary>
    public event Action<string, int?>? OpenFileRequested;

    /// <summary>Show a proposed change side by side: path, current text, proposed text.</summary>
    public event Action<string, string, string>? DiffRequested;

    /// <summary>Claude changed files (or may have, through Bash): reload editors and Git status.</summary>
    public event Action? FilesChanged;

    /// <summary>Claude is waiting for the user: bring the tool window forward.</summary>
    public event Action? AttentionRequested;

    public event Action? FocusInputRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCliMissing))]
    public partial string? CliPath { get; set; }

    public bool IsCliMissing => CliPath is null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Prompt { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial bool IsRunning { get; set; }

    public bool CanStop => IsRunning;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>"default" (ask before edits), "acceptEdits" or "plan".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel), nameof(ModeIcon))]
    public partial string PermissionMode { get; set; } = "default";

    public string ModeLabel => PermissionMode switch
    {
        "acceptEdits" => "Edit automatically",
        "plan" => "Plan mode",
        _ => "Ask before edits",
    };

    public string ModeIcon => PermissionMode switch { "acceptEdits" => "Ok", "plan" => "Pending", _ => "Pencil" };

    [ObservableProperty]
    public partial ClaudeModelChoice? SelectedModel { get; set; }

    [ObservableProperty]
    public partial bool IncludeContext { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorContext), nameof(ContextLabel))]
    public partial ClaudeEditorContext? EditorContext { get; set; }

    public bool HasEditorContext => EditorContext is not null;

    public string ContextLabel => EditorContext?.Label ?? "";

    [ObservableProperty]
    public partial bool IsHistoryOpen { get; set; }

    /// <summary>Signed-in account, from the CLI ("Claude Pro · name@example.com").</summary>
    [ObservableProperty]
    public partial string? Account { get; set; }

    /// <summary>The conversation id, once the CLI started one.</summary>
    public string? SessionId => _session?.SessionId ?? _resumeId;

    // ---- Lifecycle -----------------------------------------------------------------------------------------

    [RelayCommand]
    public void CheckCli() => CliPath = _findCli();

    /// <summary>The running CLI for the current folder, starting it once even when asked twice (warm-up, then Send).</summary>
    Task<ClaudeSession> EnsureSessionAsync()
    {
        var cwd = _workingDirectory();
        if (_session is { HasExited: false } running && _sessionCwd == cwd)
            return Task.FromResult(running);
        return _starting ??= StartSessionAsync(cwd);
    }

    async Task<ClaudeSession> StartSessionAsync(string cwd)
    {
        try
        {
            await StopSessionAsync();
            var session = await ClaudeSession.StartAsync(new ClaudeStartOptions(CliPath!, cwd, SelectedModel?.Value, PermissionMode, _resumeId));
            session.Event += ev => Dispatcher.UIThread.Post(() => Handle(session, ev));
            _session = session;
            _sessionCwd = cwd;
            ApplyInitialization(session.Initialization);
            return session;
        }
        finally
        {
            _starting = null;
        }
    }

    /// <summary>
    /// Starts the CLI when the panel is shown, as the VS Code extension does, so slash commands and models are
    /// there before the first prompt. Initializing does not call the model.
    /// </summary>
    public void WarmUp()
    {
        if (CliPath is null || _session is not null || _starting is not null)
            return;
        _ = WarmUpAsync();
    }

    async Task WarmUpAsync()
    {
        try
        {
            await EnsureSessionAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // Reported when the user sends a prompt.
        }
    }

    void ApplyInitialization(JsonElement init)
    {
        if (init.ValueKind != JsonValueKind.Object)
            return;
        if (init.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
        {
            var selected = SelectedModel?.Value;
            var list = models.EnumerateArray()
                .Select(m => new ClaudeModelChoice(ClaudeJson.Str(m, "value") ?? "", ClaudeJson.Str(m, "displayName") ?? ClaudeJson.Str(m, "value") ?? "",
                    ClaudeJson.Str(m, "description")))
                .Where(m => m.Value.Length > 0)
                .ToList();
            Models.Clear();
            foreach (var model in list)
                Models.Add(model);
            _changingModeFromCli = true;
            SelectedModel = Models.FirstOrDefault(m => m.Value == selected) ?? Models.FirstOrDefault();
            _changingModeFromCli = false;
        }
        if (init.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            Commands.Clear();
            foreach (var c in commands.EnumerateArray())
                if (ClaudeJson.Str(c, "name") is { Length: > 0 } name && !name.StartsWith("__", StringComparison.Ordinal))
                    Commands.Add(new ClaudeCommand(name, ClaudeJson.Str(c, "description") ?? "", ClaudeJson.Str(c, "argumentHint")));
        }
        if (init.TryGetProperty("account", out var account))
            Account = string.Join(" · ", new[] { ClaudeJson.Str(account, "subscriptionType"), ClaudeJson.Str(account, "email") }.Where(s => s is { Length: > 0 }));
    }

    async Task StopSessionAsync()
    {
        if (_session is not { } session)
            return;
        _resumeId = session.SessionId ?? _resumeId;
        _session = null;
        await session.DisposeAsync();
    }

    /// <summary>The solution changed: conversations belong to a folder, so start a fresh one there.</summary>
    public async Task OnWorkingDirectoryChangedAsync()
    {
        if (_sessionCwd == _workingDirectory() || _session is null && Items.Count == 0)
            return;
        await ResetAsync();
        _sessionCwd = null;
    }

    public void RequestFocus() => FocusInputRequested?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await StopSessionAsync();
    }

    // ---- Sending -------------------------------------------------------------------------------------------

    bool CanSend => Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task Send()
    {
        var text = Prompt.Trim();
        if (text.Length == 0)
            return;
        if (text is "/clear" or "/new")
        {
            Prompt = "";
            await NewConversationAsync();
            return;
        }
        CheckCli();
        if (CliPath is null)
            return;
        Prompt = "";
        Suggestions.Clear();
        IsHistoryOpen = false;
        var context = IncludeContext ? _editorContext() : null;
        Items.Add(new UserChatItem(text, context?.Label));
        BeginTurn();
        try
        {
            var session = await EnsureSessionAsync();
            await session.SendUserMessageAsync(text, context is null ? null : [ContextBlock(context)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            EndTurn();
            Items.Add(new NoticeChatItem("Claude Code could not be started: " + ex.Message, isError: true));
        }
    }

    /// <summary>The editor context, appended to the prompt like the VS Code extension's selection context.</summary>
    internal static string ContextBlock(ClaudeEditorContext context)
    {
        var sb = new StringBuilder("<ide_context>\nThe user has ").Append(context.FilePath).Append(" open in the editor");
        if (context.StartLine is { } start && context.EndLine is { } end && context.SelectedText is { Length: > 0 } selected)
        {
            sb.Append($" and selected lines {start}-{end}:\n```\n").Append(selected.TrimEnd('\n')).Append("\n```");
        }
        else
        {
            sb.Append('.');
        }
        return sb.Append("\n</ide_context>").ToString();
    }


    [RelayCommand]
    async Task Stop()
    {
        if (_session is not { } session || !IsRunning)
            return;
        CancelPendingCards("Cancelled");
        try
        {
            await session.InterruptAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    [RelayCommand]
    public async Task NewConversationAsync()
    {
        await ResetAsync();
        FocusInputRequested?.Invoke();
    }

    async Task ResetAsync()
    {
        await StopSessionAsync();
        _resumeId = null;
        EndTurn();
        Items.Clear();
        _tools.Clear();
        IsHistoryOpen = false;
    }

    void BeginTurn()
    {
        IsRunning = true;
        _turnStarted = DateTime.Now;
        _tokens = 0;
        _thinking = false;
        UpdateStatus();
        _timer.Start();
    }

    void EndTurn()
    {
        IsRunning = false;
        _timer.Stop();
        StatusText = "";
    }

    void UpdateStatus()
    {
        if (!IsRunning)
            return;
        var waiting = Items.Any(i => i is PermissionChatItem { IsPending: true } or QuestionChatItem { IsPending: true } or PlanChatItem { IsPending: true });
        var verb = waiting ? "Waiting for you" : _thinking ? "Thinking" : "Working";
        var seconds = (int)(DateTime.Now - _turnStarted).TotalSeconds;
        StatusText = _tokens > 0 ? $"{verb}... ({seconds}s · {FormatTokens(_tokens)} tokens)" : $"{verb}... ({seconds}s)";
    }

    static string FormatTokens(int n) => n >= 1000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    // ---- Events from the CLI -------------------------------------------------------------------------------

    void Handle(ClaudeSession session, ClaudeEvent ev)
    {
        if (session != _session)
            return;
        Apply(ev, live: true);
    }

    /// <summary>Applies one event to the conversation; <paramref name="live"/> is false when replaying a saved transcript.</summary>
    void Apply(ClaudeEvent ev, bool live)
    {
        switch (ev)
        {
            case ClaudeInit init:
                _resumeId = init.SessionId;
                OnPropertyChanged(nameof(SessionId));
                break;

            case ClaudeUserText user:
                var text = ClaudeHistory.StripContext(user.Text);
                if (text.Length > 0)
                    Items.Add(new UserChatItem(text, user.Text.Contains("<ide_context>", StringComparison.Ordinal) ? "editor context" : null));
                break;

            case ClaudeBlockStart start:
                _thinking = start.BlockType == "thinking";
                if (start.BlockType == "text")
                    Items.Add(new TextChatItem("") { StreamKey = $"{start.MessageId}:{start.Index}" });
                UpdateStatus();
                break;

            case ClaudeBlockDelta delta:
                var key = $"{delta.MessageId}:{delta.Index}";
                if (delta.Kind == "text" && Items.OfType<TextChatItem>().LastOrDefault(t => t.StreamKey == key) is { } streaming)
                {
                    streaming.Text += delta.Text;
                }
                else if (delta.Kind == "thinking" && delta.Text.Length > 0)
                {
                    if (Items.OfType<ThinkingChatItem>().LastOrDefault(t => t.StreamKey == key) is { } thinking)
                        thinking.Text += delta.Text;
                    else
                        Items.Add(new ThinkingChatItem(delta.Text) { StreamKey = key });
                }
                break;

            case ClaudeTokens tokens:
                _tokens = Math.Max(_tokens, tokens.OutputTokens);
                UpdateStatus();
                break;

            case ClaudeAssistant assistant when assistant.ParentToolUseId is null:
                foreach (var block in assistant.Blocks)
                    AddBlock(assistant.MessageId, block, live);
                _thinking = false;
                break;

            case ClaudeToolResults results when results.ParentToolUseId is null:
                var changed = false;
                foreach (var result in results.Results)
                {
                    if (!_tools.TryGetValue(result.ToolUseId, out var tool))
                        continue;
                    tool.State = result.IsError ? ToolState.Error : ToolState.Done;
                    tool.Result = result.Content;
                    tool.Summary = tool.WasDenied ? "Denied by you" : Summarize(tool, result.Content, result.IsError);
                    changed |= !result.IsError && FileChangingTools.Contains(tool.Name);
                }
                if (changed && live)
                    FilesChanged?.Invoke();
                break;

            case ClaudePermissionRequest request:
                AddPermissionCard(request);
                UpdateStatus();
                AttentionRequested?.Invoke();
                break;

            case ClaudeNotice notice:
                Items.Add(new NoticeChatItem(notice.Text, notice.IsError));
                break;

            case ClaudeResult result:
                _resumeId = result.SessionId ?? _resumeId;
                CancelPendingCards("Cancelled");
                // Drop text blocks that never received content (a stream cut short).
                foreach (var empty in Items.OfType<TextChatItem>().Where(t => t.Text.Length == 0).ToList())
                    Items.Remove(empty);
                if (result.IsError || result.Subtype != "success")
                {
                    var message = result.Subtype switch
                    {
                        "error_max_turns" => "Claude stopped: the maximum number of turns was reached.",
                        "error_during_execution" when result.Text is null => "Interrupted.",
                        _ => result.Text ?? "Claude Code reported an error.",
                    };
                    Items.Add(new NoticeChatItem(message, isError: message != "Interrupted."));
                }
                foreach (var tool in _tools.Values.Where(t => t.IsRunning))
                    tool.State = ToolState.Error;
                EndTurn();
                break;

            case ClaudeExited exited:
                _session = null;
                if (IsRunning || exited.ExitCode != 0)
                {
                    var tail = string.Join("\n", exited.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
                    Items.Add(new NoticeChatItem($"Claude Code stopped (exit code {exited.ExitCode}).{(tail.Length > 0 ? "\n" + tail : "")}", isError: true));
                }
                CancelPendingCards("Cancelled");
                EndTurn();
                break;
        }
    }

    void AddBlock(string messageId, ClaudeBlock block, bool live)
    {
        switch (block)
        {
            case ClaudeTextBlock text when text.Text.Trim().Length > 0:
                // The streamed copy of this block gets its final text; otherwise (replay, no streaming) add it.
                if (Items.OfType<TextChatItem>().FirstOrDefault(t => t.StreamKey?.StartsWith(messageId + ":", StringComparison.Ordinal) == true) is { } streamed)
                {
                    streamed.Text = text.Text;
                    streamed.StreamKey = null;
                }
                else
                {
                    Items.Add(new TextChatItem(text.Text));
                }
                break;
            case ClaudeThinkingBlock thinking when thinking.Text.Trim().Length > 0:
                if (Items.OfType<ThinkingChatItem>().FirstOrDefault(t => t.StreamKey?.StartsWith(messageId + ":", StringComparison.Ordinal) == true) is { } streamedThought)
                {
                    streamedThought.Text = thinking.Text;
                    streamedThought.StreamKey = null;
                }
                else
                {
                    Items.Add(new ThinkingChatItem(thinking.Text));
                }
                break;
            case ClaudeToolUseBlock tool when tool.Name is not ("AskUserQuestion" or "ExitPlanMode"):
                var item = BuildTool(tool, live);
                _tools[tool.Id] = item;
                Items.Add(item);
                break;
        }
    }

    // ---- Tool calls ----------------------------------------------------------------------------------------

    string Relative(string? path)
    {
        if (path is null)
            return "";
        var cwd = _sessionCwd ?? _workingDirectory();
        return path.StartsWith(cwd + "/", StringComparison.Ordinal) ? path[(cwd.Length + 1)..] : path;
    }

    static string FirstLine(string? text, int max = 100)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > max ? line[..(max - 3)] + "..." : line;
    }

    ToolChatItem BuildTool(ClaudeToolUseBlock tool, bool live)
    {
        var input = tool.Input;
        string? Str(string name) => ClaudeJson.Str(input, name);
        switch (tool.Name)
        {
            case "Read":
                return new ToolChatItem(tool.Id, tool.Name, "Read", Relative(Str("file_path")), Str("file_path"));
            case "Write":
            {
                var path = Str("file_path");
                var content = Str("content") ?? "";
                var before = live && path is not null && File.Exists(path) ? SafeRead(path) : null;
                return new ToolChatItem(tool.Id, tool.Name, "Write", Relative(path), path)
                {
                    Diff = before is null ? UnifiedDiff.Added(content, 60) : UnifiedDiff.Compute(before, content, maxLines: 60),
                };
            }
            case "Edit":
            {
                var path = Str("file_path");
                return new ToolChatItem(tool.Id, tool.Name, "Update", Relative(path), path)
                {
                    Diff = UnifiedDiff.Compute(Str("old_string") ?? "", Str("new_string") ?? "", context: 1, maxLines: 60),
                };
            }
            case "MultiEdit":
            {
                var path = Str("file_path");
                var diff = new List<DiffLine>();
                if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                    foreach (var edit in edits.EnumerateArray())
                    {
                        if (diff.Count > 0)
                            diff.Add(new DiffLine(DiffLineKind.Gap, "..."));
                        diff.AddRange(UnifiedDiff.Compute(ClaudeJson.Str(edit, "old_string") ?? "", ClaudeJson.Str(edit, "new_string") ?? "", context: 1, maxLines: 30));
                    }
                return new ToolChatItem(tool.Id, tool.Name, "Update", Relative(path), path) { Diff = diff };
            }
            case "NotebookEdit":
                return new ToolChatItem(tool.Id, tool.Name, "Edit Notebook", Relative(Str("notebook_path")), Str("notebook_path"));
            case "Bash":
            {
                var command = Str("command") ?? "";
                return new ToolChatItem(tool.Id, tool.Name, "Bash", FirstLine(command, 80), null)
                {
                    Code = command.Contains('\n') || command.Length > 80 ? command : null,
                };
            }
            case "Glob":
                return new ToolChatItem(tool.Id, tool.Name, "Search", Str("pattern") + (Str("path") is { } p ? " in " + Relative(p) : ""), null);
            case "Grep":
                return new ToolChatItem(tool.Id, tool.Name, "Search", $"\"{Str("pattern")}\"" + (Str("path") is { } gp ? " in " + Relative(gp) : ""), null);
            case "WebFetch":
                return new ToolChatItem(tool.Id, tool.Name, "Fetch", Str("url"), null);
            case "WebSearch":
                return new ToolChatItem(tool.Id, tool.Name, "Web Search", Str("query"), null);
            case "Task" or "Agent":
                return new ToolChatItem(tool.Id, tool.Name, "Task", Str("description"), null) { Code = Str("prompt") };
            case "TodoWrite":
            {
                var todos = input.TryGetProperty("todos", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(t => new TodoEntry(ClaudeJson.Str(t, "content") ?? "", ClaudeJson.Str(t, "status") ?? "pending")).ToList()
                    : [];
                return new ToolChatItem(tool.Id, tool.Name, "Update Todos", null, null) { Todos = todos };
            }
            default:
            {
                var title = tool.Name;
                if (tool.Name.StartsWith("mcp__", StringComparison.Ordinal) && tool.Name.Split("__") is { Length: >= 3 } parts)
                    title = $"{parts[1]} - {string.Join("__", parts[2..])}";
                var json = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
                return new ToolChatItem(tool.Id, tool.Name, title, json.Length > 90 ? json[..87] + "..." : json, null)
                {
                    Code = json.Length > 90 ? Pretty(input) : null,
                };
            }
        }
    }

    static string Summarize(ToolChatItem tool, string content, bool isError)
    {
        if (isError)
            return FirstLine(content, 140);
        var lines = content.Split('\n').Count(l => l.Trim().Length > 0);
        return tool.Name switch
        {
            "Read" => $"Read {lines} line{(lines == 1 ? "" : "s")}",
            "Write" or "Edit" or "MultiEdit" => ChangeSummary(tool),
            "Glob" => lines == 0 || content.StartsWith("No files", StringComparison.Ordinal) ? "No files found" : $"Found {lines} file{(lines == 1 ? "" : "s")}",
            "Grep" => lines == 0 || content.StartsWith("No ", StringComparison.Ordinal) ? "No matches" : $"Found {lines} line{(lines == 1 ? "" : "s")}",
            "TodoWrite" => "",
            "Bash" => lines == 0 ? "(No output)" : FirstLine(content, 140),
            _ => FirstLine(content, 140),
        };
    }

    static string ChangeSummary(ToolChatItem tool)
    {
        var added = tool.Diff.Count(d => d.Kind == DiffLineKind.Added);
        var removed = tool.Diff.Count(d => d.Kind == DiffLineKind.Removed);
        if (tool.Name == "Write" && removed == 0)
            return $"Wrote {added} line{(added == 1 ? "" : "s")}";
        return $"{added} addition{(added == 1 ? "" : "s")}, {removed} removal{(removed == 1 ? "" : "s")}";
    }

    static string Pretty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
            return "";
        var text = JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        return text.Length > 4000 ? text[..4000] + "\n..." : text;
    }

    static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [RelayCommand]
    void ToggleExpand(ChatItem? item)
    {
        if (item is ToolChatItem tool)
            tool.IsExpanded = !tool.IsExpanded;
        else if (item is ThinkingChatItem thinking)
            thinking.IsExpanded = !thinking.IsExpanded;
    }

    [RelayCommand]
    void OpenFile(ToolChatItem? tool)
    {
        if (tool?.FilePath is { } path && File.Exists(path))
            OpenFileRequested?.Invoke(path, null);
    }

    // ---- Permission prompts, questions and plans -----------------------------------------------------------

    void AddPermissionCard(ClaudePermissionRequest request)
    {
        var input = request.Input;
        if (request.ToolName == "AskUserQuestion")
        {
            var questions = input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array
                ? qs.EnumerateArray().Select(q => new QuestionEntry(ClaudeJson.Str(q, "question") ?? "", ClaudeJson.Str(q, "header"), ClaudeJson.Bool(q, "multiSelect"),
                    q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                        ? opts.EnumerateArray().Select(o => new QuestionOption(ClaudeJson.Str(o, "label") ?? "", ClaudeJson.Str(o, "description")))
                        : [])).ToList()
                : [];
            Items.Add(new QuestionChatItem(request, questions));
            return;
        }
        if (request.ToolName == "ExitPlanMode")
        {
            Items.Add(new PlanChatItem(request, ClaudeJson.Str(input, "plan") ?? ""));
            return;
        }
        if (request.ToolUseId is { } id && _tools.TryGetValue(id, out var tool))
            tool.State = ToolState.WaitingForApproval;
        Items.Add(BuildPermission(request));
    }

    PermissionChatItem BuildPermission(ClaudePermissionRequest request)
    {
        var input = request.Input;
        string? Str(string name) => ClaudeJson.Str(input, name);
        var sessionLabel = SessionLabel(request.Suggestions);
        var file = Str("file_path") ?? Str("notebook_path");
        var name = file is null ? null : Path.GetFileName(file);
        switch (request.ToolName)
        {
            case "Bash":
                return new PermissionChatItem(request, "Allow Claude to run this command?", sessionLabel)
                {
                    Code = Str("command"),
                    Description = Str("description"),
                };
            case "Write" when file is not null:
            {
                var before = File.Exists(file) ? SafeRead(file) : null;
                var after = Str("content") ?? "";
                return new PermissionChatItem(request, before is null ? $"Allow Claude to create {name}?" : $"Allow Claude to overwrite {name}?", sessionLabel)
                {
                    Description = Relative(file),
                    Diff = before is null ? UnifiedDiff.Added(after, 40) : UnifiedDiff.Compute(before, after, maxLines: 40),
                    Proposal = (file, before ?? "", after),
                };
            }
            case "Edit" or "MultiEdit" when file is not null:
            {
                var before = SafeRead(file) ?? "";
                var after = ApplyEdits(before, input, request.ToolName);
                return new PermissionChatItem(request, $"Allow Claude to make this edit to {name}?", sessionLabel)
                {
                    Description = Relative(file),
                    Diff = UnifiedDiff.Compute(before, after, maxLines: 40),
                    Proposal = (file, before, after),
                };
            }
            case "WebFetch":
                return new PermissionChatItem(request, "Allow Claude to fetch this page?", sessionLabel) { Code = Str("url"), Description = Str("prompt") };
            default:
                return new PermissionChatItem(request, $"Allow Claude to use {request.DisplayName ?? request.ToolName}?", sessionLabel)
                {
                    Description = request.Description,
                    Code = Pretty(input),
                };
        }
    }

    /// <summary>The file as it will be after an Edit / MultiEdit, for the diff preview.</summary>
    internal static string ApplyEdits(string text, JsonElement input, string toolName)
    {
        IEnumerable<JsonElement> edits = toolName == "MultiEdit" && input.TryGetProperty("edits", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [input];
        foreach (var edit in edits)
        {
            var oldString = ClaudeJson.Str(edit, "old_string") ?? "";
            var newString = ClaudeJson.Str(edit, "new_string") ?? "";
            if (oldString.Length == 0)
                continue;
            if (ClaudeJson.Bool(edit, "replace_all"))
            {
                text = text.Replace(oldString, newString, StringComparison.Ordinal);
            }
            else
            {
                var at = text.IndexOf(oldString, StringComparison.Ordinal);
                if (at >= 0)
                    text = text[..at] + newString + text[(at + oldString.Length)..];
            }
        }
        return text;
    }

    /// <summary>The "don't ask again" option the CLI suggests, worded like Claude Code's own prompt.</summary>
    internal static string? SessionLabel(IReadOnlyList<JsonElement> suggestions)
    {
        foreach (var s in suggestions)
        {
            switch (ClaudeJson.Str(s, "type"))
            {
                case "setMode" when ClaudeJson.Str(s, "mode") == "acceptEdits":
                    return "Yes, allow all edits during this session";
                case "setMode" when ClaudeJson.Str(s, "mode") is { } mode:
                    return $"Yes, and switch to {mode} mode";
                case "addRules" when s.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() > 0:
                    var rule = rules[0];
                    var toolName = ClaudeJson.Str(rule, "toolName") ?? "this tool";
                    var where = ClaudeJson.Str(s, "destination") is "session" ? "this session" : "this project";
                    return ClaudeJson.Str(rule, "ruleContent") is { Length: > 0 } content
                        ? $"Yes, and don't ask again for {toolName}({content}) in {where}"
                        : $"Yes, and don't ask again for {toolName} in {where}";
                case "addDirectories" when s.TryGetProperty("directories", out var dirs) && dirs.ValueKind == JsonValueKind.Array && dirs.GetArrayLength() > 0:
                    return $"Yes, and allow access to {dirs[0].GetString()}";
            }
        }
        return null;
    }

    async Task RespondAsync(ClaudePermissionRequest request, ClaudePermissionDecision decision)
    {
        if (_session is not { } session)
            return;
        try
        {
            await session.RespondToPermissionAsync(request.RequestId, decision);
        }
        catch (InvalidOperationException ex)
        {
            Items.Add(new NoticeChatItem(ex.Message, isError: true));
        }
        UpdateStatus();
    }

    [RelayCommand]
    async Task Allow(PermissionChatItem? item)
    {
        if (item is not { IsPending: true })
            return;
        Resolve(item, "Allowed");
        await RespondAsync(item.Request, ClaudePermissionDecision.AllowOnce(item.Request.Input));
    }

    [RelayCommand]
    async Task AllowForSession(PermissionChatItem? item)
    {
        if (item is not { IsPending: true })
            return;
        Resolve(item, item.SessionLabel?.Replace("Yes, ", "Allowed, ") ?? "Allowed");
        if (item.Request.Suggestions.FirstOrDefault(s => ClaudeJson.Str(s, "type") == "setMode") is { ValueKind: JsonValueKind.Object } mode
            && ClaudeJson.Str(mode, "mode") is { } newMode)
            SetModeFromCli(newMode);
        await RespondAsync(item.Request, new ClaudePermissionDecision(true, item.Request.Input, item.Request.Suggestions));
    }

    [RelayCommand]
    async Task Deny(PermissionChatItem? item)
    {
        if (item is not { IsPending: true })
            return;
        var feedback = item.Feedback.Trim();
        Resolve(item, feedback.Length > 0 ? $"Denied: {feedback}" : "Denied");
        if (item.Request.ToolUseId is { } id && _tools.TryGetValue(id, out var tool))
        {
            tool.WasDenied = true;
            tool.State = ToolState.Error;
        }
        // Without feedback Claude stops and waits, as when you press "No" in Claude Code.
        await RespondAsync(item.Request, feedback.Length > 0
            ? ClaudePermissionDecision.Deny("The user doesn't want to proceed with this tool use. They said: " + feedback)
            : new ClaudePermissionDecision(false, Message: "The user doesn't want to proceed with this tool use. Wait for their next instruction.", Interrupt: true));
    }

    void Resolve(PermissionChatItem item, string outcome)
    {
        item.IsResolved = true;
        item.Outcome = outcome;
        if (item.Request.ToolUseId is { } id && _tools.TryGetValue(id, out var tool) && tool.State == ToolState.WaitingForApproval)
            tool.State = ToolState.Running;
    }

    [RelayCommand]
    void OpenDiff(PermissionChatItem? item)
    {
        if (item?.Proposal is { } p)
            DiffRequested?.Invoke(p.Path, p.Before, p.After);
    }

    [RelayCommand]
    async Task SubmitAnswers(QuestionChatItem? item)
    {
        if (item is not { IsPending: true })
            return;
        if (item.Questions.Any(q => q.Answer is null))
        {
            item.Outcome = "Answer each question first.";
            return;
        }
        var answers = new JsonObject();
        foreach (var q in item.Questions)
            answers[q.Question] = q.Answer;
        var updated = JsonNode.Parse(item.Request.Input.GetRawText()) as JsonObject ?? new JsonObject();
        updated["answers"] = answers;
        item.IsResolved = true;
        item.Outcome = string.Join("\n", item.Questions.Select(q => $"{q.Question} {q.Answer}"));
        using var doc = JsonDocument.Parse(updated.ToJsonString());
        await RespondAsync(item.Request, ClaudePermissionDecision.AllowOnce(doc.RootElement.Clone()));
    }

    [RelayCommand]
    async Task ApprovePlan(PlanChatItem? item) => await ResolvePlanAsync(item, approve: true, autoAccept: true);

    [RelayCommand]
    async Task ApprovePlanManually(PlanChatItem? item) => await ResolvePlanAsync(item, approve: true, autoAccept: false);

    [RelayCommand]
    async Task KeepPlanning(PlanChatItem? item) => await ResolvePlanAsync(item, approve: false, autoAccept: false);

    async Task ResolvePlanAsync(PlanChatItem? item, bool approve, bool autoAccept)
    {
        if (item is not { IsPending: true })
            return;
        item.IsResolved = true;
        if (!approve)
        {
            var feedback = item.Feedback.Trim();
            item.Outcome = feedback.Length > 0 ? $"Keep planning: {feedback}" : "Keep planning";
            await RespondAsync(item.Request, feedback.Length > 0
                ? ClaudePermissionDecision.Deny("The user wants to keep planning. They said: " + feedback)
                : new ClaudePermissionDecision(false, Message: "The user wants to keep planning. Wait for their next instruction.", Interrupt: true));
            return;
        }
        item.Outcome = autoAccept ? "Approved, editing automatically" : "Approved, asking before edits";
        var mode = autoAccept ? "acceptEdits" : "default";
        SetModeFromCli(mode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { type = "setMode", mode, destination = "session" } }));
        await RespondAsync(item.Request, new ClaudePermissionDecision(true, item.Request.Input, doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()));
    }

    void CancelPendingCards(string outcome)
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case PermissionChatItem { IsPending: true } p:
                    p.IsResolved = true;
                    p.Outcome = outcome;
                    break;
                case QuestionChatItem { IsPending: true } q:
                    q.IsResolved = true;
                    q.Outcome = outcome;
                    break;
                case PlanChatItem { IsPending: true } plan:
                    plan.IsResolved = true;
                    plan.Outcome = outcome;
                    break;
            }
        }
    }

    // ---- Mode, model, context ------------------------------------------------------------------------------

    [RelayCommand]
    void CycleMode() => PermissionMode = PermissionMode switch { "default" => "acceptEdits", "acceptEdits" => "plan", _ => "default" };

    void SetModeFromCli(string mode)
    {
        _changingModeFromCli = true;
        PermissionMode = mode is "acceptEdits" or "plan" ? mode : "default";
        _changingModeFromCli = false;
    }

    partial void OnPermissionModeChanged(string value)
    {
        _settings.ClaudePermissionMode = value;
        _settings.Save();
        if (!_changingModeFromCli && _session is { HasExited: false } session)
            _ = SendControlAsync(() => session.SetPermissionModeAsync(value));
    }

    partial void OnSelectedModelChanged(ClaudeModelChoice? value)
    {
        if (value is null || _changingModeFromCli)
            return;
        _settings.ClaudeModel = value.Value;
        _settings.Save();
        if (_session is { HasExited: false } session)
            _ = SendControlAsync(() => session.SetModelAsync(value.Value));
    }

    async Task SendControlAsync(Func<Task> request)
    {
        try
        {
            await request();
        }
        catch (InvalidOperationException ex)
        {
            Items.Add(new NoticeChatItem(ex.Message, isError: true));
        }
    }

    [RelayCommand]
    void ToggleContext() => IncludeContext = !IncludeContext;

    /// <summary>Re-reads the active editor and selection for the context chip.</summary>
    public void RefreshContext() => EditorContext = _editorContext();

    // ---- History -------------------------------------------------------------------------------------------

    [RelayCommand]
    void ToggleHistory()
    {
        IsHistoryOpen = !IsHistoryOpen;
        if (IsHistoryOpen)
            RefreshHistory();
    }

    public void RefreshHistory()
    {
        History.Clear();
        foreach (var conversation in ClaudeHistory.List(_workingDirectory()))
            History.Add(conversation);
    }

    [RelayCommand]
    public async Task ResumeConversationAsync(ClaudeConversation? conversation)
    {
        if (conversation is null)
            return;
        await StopSessionAsync();
        EndTurn();
        Items.Clear();
        _tools.Clear();
        _sessionCwd = _workingDirectory();
        foreach (var ev in ClaudeHistory.Load(conversation.FilePath))
            Apply(ev, live: false);
        // Calls that never got a result in the transcript are shown as finished, not as still running.
        foreach (var tool in _tools.Values.Where(t => t.IsRunning))
            tool.State = ToolState.Done;
        _resumeId = conversation.SessionId;
        OnPropertyChanged(nameof(SessionId));
        IsHistoryOpen = false;
        FocusInputRequested?.Invoke();
    }

    // ---- Completion ----------------------------------------------------------------------------------------

    partial void OnPromptChanged(string value) => UpdateSuggestions();

    void UpdateSuggestions()
    {
        Suggestions.Clear();
        var prompt = Prompt;
        if (prompt.StartsWith('/') && !prompt.Contains(' ') && !prompt.Contains('\n'))
        {
            var typed = prompt[1..];
            foreach (var c in Commands.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(8))
                Suggestions.Add(new ClaudeSuggestion("/" + c.Name + " ", "/" + c.Name, c.Description, IsFile: false));
            return;
        }
        if (FileMention().Match(prompt) is { Success: true } mention)
        {
            var typed = mention.Groups[1].Value;
            var cwd = _workingDirectory();
            var files = _projectFiles()
                .Select(f => f.StartsWith(cwd + "/", StringComparison.Ordinal) ? f[(cwd.Length + 1)..] : f)
                .Where(f => typed.Length == 0 || f.Contains(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f).StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f.Length)
                .Take(8);
            foreach (var file in files)
                Suggestions.Add(new ClaudeSuggestion("@" + file + " ", Path.GetFileName(file), file, IsFile: true));
        }
    }

    [GeneratedRegex(@"(?:^|\s)@(\S*)$")]
    private static partial Regex FileMention();

    [RelayCommand]
    void ApplySuggestion(ClaudeSuggestion? suggestion)
    {
        if (suggestion is null)
            return;
        Prompt = suggestion.IsFile
            ? Prompt[..Prompt.LastIndexOf('@')] + suggestion.Insert
            : suggestion.Insert;
        Suggestions.Clear();
        FocusInputRequested?.Invoke();
    }
}
