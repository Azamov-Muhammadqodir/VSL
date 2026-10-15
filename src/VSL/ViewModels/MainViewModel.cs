using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Build;
using VSL.Editor;
using VSL.Lsp;
using VSL.Services;
using VSL.Workspace;

namespace VSL.ViewModels;

public enum StatusKind
{
    NoSolution,
    Ready,
    Busy,
    Running,
}

/// <summary>Dialogs the view provides to the view model.</summary>
public interface IDialogService
{
    Task<string?> PickSolutionAsync();
    Task<string?> PromptAsync(string title, string label, string initial);
    /// <summary>Returns true = save, false = discard, null = cancel.</summary>
    Task<bool?> ConfirmSaveAsync(string fileName);
    /// <summary>Yes/No question; returns true for Yes.</summary>
    Task<bool?> ConfirmAsync(string title, string message);
}

public partial class MainViewModel : ObservableObject
{
    readonly List<ErrorItem> _buildErrors = [];
    RoslynClient? _lsp;
    CancellationTokenSource? _buildCts;
    ProcessRunner? _runProcess;

    public MainViewModel()
    {
        BuildOutput = new OutputChannel("Build");
        DebugOutput = new OutputChannel("Debug");
        ServerOutput = new OutputChannel("Language Server");
        OutputChannels = [BuildOutput, DebugOutput, ServerOutput, GitOutput];
        SelectedOutput = BuildOutput;
        InitializeDebugging();
        EditorHost.DefaultZoom = Math.Clamp(Settings.EditorZoom, EditorHost.MinZoom, EditorHost.MaxZoom);
    }

    public IDialogService? Dialogs { get; set; }
    public Settings Settings { get; } = Settings.Load();

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = [];
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public ObservableCollection<ErrorItem> Errors { get; } = [];
    public ObservableCollection<FindResultItem> FindResults { get; } = [];
    public ObservableCollection<ProjectInfo> StartupProjects { get; } = [];
    public ObservableCollection<string> LaunchProfiles { get; } = [];
    public ObservableCollection<OutputChannel> OutputChannels { get; }
    public IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];
    public IReadOnlyList<string> RecentSolutions => Settings.RecentSolutions;

    public OutputChannel BuildOutput { get; }
    public OutputChannel DebugOutput { get; }
    public OutputChannel ServerOutput { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(HasSolution))]
    [NotifyCanExecuteChangedFor(nameof(BuildSolutionCommand), nameof(RebuildSolutionCommand), nameof(CleanSolutionCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand), nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand))]
    public partial SolutionInfo? Solution { get; set; }

    [ObservableProperty]
    public partial DocumentViewModel? ActiveDocument { get; set; }

    [ObservableProperty]
    public partial OutputChannel SelectedOutput { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial StatusKind Status { get; set; } = StatusKind.NoSolution;

    [ObservableProperty]
    public partial string CaretText { get; set; } = "";

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildSolutionCommand), nameof(RebuildSolutionCommand), nameof(CleanSolutionCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand), nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    public partial ProjectInfo? StartupProject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    public partial string? LaunchProfile { get; set; }

    [ObservableProperty]
    public partial string Configuration { get; set; } = "Debug";

    /// <summary>0 = Error List, 1 = Output, 2 = Find Symbol Results.</summary>
    [ObservableProperty]
    public partial int BottomTabIndex { get; set; }

    [ObservableProperty]
    public partial string FindResultsTitle { get; set; } = "Find Symbol Results";

    [ObservableProperty]
    public partial bool ShowErrors { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarnings { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowMessages { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorCountText))]
    public partial int ErrorCount { get; set; }

    public string ErrorCountText => ErrorCount == 1 ? "1 Error" : $"{ErrorCount} Errors";

    public string WarningCountText => WarningCount == 1 ? "1 Warning" : $"{WarningCount} Warnings";

    public string MessageCountText => MessageCount == 1 ? "1 Message" : $"{MessageCount} Messages";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningCountText))]
    public partial int WarningCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCountText))]
    public partial int MessageCount { get; set; }

    [ObservableProperty]
    public partial string ExplorerFilter { get; set; } = "";

    public bool HasSolution => Solution is not null;

    public string WindowTitle => Solution is null ? "VSL" : $"{Solution.Name} - VSL";

    partial void OnShowErrorsChanged(bool value) => RefreshErrorList();
    partial void OnShowWarningsChanged(bool value) => RefreshErrorList();
    partial void OnShowMessagesChanged(bool value) => RefreshErrorList();

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        UpdateCaretText();
        // Like VS, refresh squiggles for the document the user switches to.
        value?.Host.ScheduleDiagnostics(150);
        if (Solution is not null && value is not null)
            Settings.For(Solution.FilePath).ActiveDocument = value.FilePath;
    }

    partial void OnStartupProjectChanged(ProjectInfo? value)
    {
        LaunchProfiles.Clear();
        foreach (var p in value?.LaunchProfiles ?? [])
            LaunchProfiles.Add(p.Name);
        var saved = Solution is null ? null : Settings.For(Solution.FilePath).LaunchProfile;
        LaunchProfile = LaunchProfiles.Contains(saved ?? "") ? saved : LaunchProfiles.FirstOrDefault();
        if (Solution is not null && value is not null)
            Settings.For(Solution.FilePath).StartupProject = value.FilePath;
    }

    partial void OnLaunchProfileChanged(string? value)
    {
        if (Solution is not null && value is not null)
            Settings.For(Solution.FilePath).LaunchProfile = value;
    }

    partial void OnConfigurationChanged(string value)
    {
        if (Solution is not null)
            Settings.For(Solution.FilePath).Configuration = value;
    }

    void SetStatus(string text, StatusKind? kind = null)
    {
        StatusText = text;
        if (kind is { } k)
            Status = k;
    }

    // ---- Solution lifecycle --------------------------------------------------------------------------------

    [RelayCommand]
    async Task OpenSolution()
    {
        if (Dialogs is null || await Dialogs.PickSolutionAsync() is not { } path)
            return;
        await OpenSolutionAsync(path);
    }

    [RelayCommand]
    Task OpenRecent(string path) => OpenSolutionAsync(path);

    public async Task OpenSolutionAsync(string path)
    {
        if (!await CloseSolutionCoreAsync())
            return;
        SetStatus($"Opening {Path.GetFileName(path)}...", StatusKind.Busy);
        SolutionInfo solution;
        try
        {
            solution = await SolutionLoader.LoadAsync(path);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open solution: {ex.Message}", StatusKind.NoSolution);
            return;
        }

        Solution = solution;
        ExplorerRoots.Add(solution.Root);
        Settings.AddRecent(path);
        OnPropertyChanged(nameof(RecentSolutions));
        var state = Settings.For(path);
        Configuration = state.Configuration;
        LoadDebugState(state);

        foreach (var p in solution.Projects.Where(p => p.IsRunnable))
            StartupProjects.Add(p);
        StartupProject = StartupProjects.FirstOrDefault(p => p.FilePath == state.StartupProject)
            ?? StartupProjects.FirstOrDefault(p => p.IsWeb)
            ?? StartupProjects.FirstOrDefault();

        foreach (var file in state.OpenDocuments.Where(File.Exists).ToList())
            OpenDocument(file, activate: false);
        ActiveDocument = Documents.FirstOrDefault(d => d.FilePath == state.ActiveDocument) ?? Documents.FirstOrDefault();
        Settings.Save();

        SetStatus("Ready", StatusKind.Ready);
        await OpenGitAsync(solution);
        // New shells start in the solution folder, like VS's Developer terminal.
        if (_terminal is { } terminal)
            terminal.WorkingDirectory = solution.Directory;
        await StartLanguageServerAsync();
    }

    [RelayCommand]
    async Task CloseSolution() => await CloseSolutionCoreAsync();

    /// <summary>Closes everything; returns false if the user cancelled saving.</summary>
    public async Task<bool> CloseSolutionCoreAsync()
    {
        foreach (var doc in Documents.Where(d => d.IsDirty).ToList())
        {
            if (Dialogs is null)
                break;
            var answer = await Dialogs.ConfirmSaveAsync(doc.FileName);
            if (answer is null)
                return false;
            if (answer == true)
                doc.Host.Save();
        }
        if (Solution is not null)
        {
            var state = Settings.For(Solution.FilePath);
            state.OpenDocuments = Documents.Select(d => d.FilePath).ToList();
            state.ActiveDocument = ActiveDocument?.FilePath;
            SaveDebugState(state);
            Settings.Save();
        }

        _runProcess?.Kill();
        if (_session is { } debugSession)
            await debugSession.TerminateAsync();
        _buildCts?.Cancel();
        foreach (var doc in Documents)
            doc.Dispose();
        Documents.Clear();
        ActiveDocument = null;
        await StopLanguageServerAsync();
        ExplorerRoots.Clear();
        StartupProjects.Clear();
        LaunchProfiles.Clear();
        _buildErrors.Clear();
        FindResults.Clear();
        RefreshErrorList();
        Breakpoints.Load(new Dictionary<string, List<int>>());
        Watches.Clear();
        ResetTesting();
        CloseGit();
        Solution = null;
        SetStatus("Ready", StatusKind.NoSolution);
        return true;
    }

    public void SaveSession()
    {
        if (Solution is null)
            return;
        var state = Settings.For(Solution.FilePath);
        state.OpenDocuments = Documents.Select(d => d.FilePath).ToList();
        state.ActiveDocument = ActiveDocument?.FilePath;
        SaveDebugState(state);
        Settings.Save();
    }

    // ---- Language server -------------------------------------------------------------------------------------

    async Task StartLanguageServerAsync()
    {
        if (Solution is not { } solution)
            return;
        ServerStatus = "IntelliSense: starting...";
        try
        {
            var install = await RoslynServerLocator.EnsureInstalledAsync(ServerOutput.Append, CancellationToken.None);
            var logDir = Path.Combine(Path.GetTempPath(), "VSL", "roslyn-logs");
            var lsp = await RoslynClient.StartAsync(install, solution.FilePath, logDir, CancellationToken.None);
            if (Solution != solution)
            {
                await lsp.DisposeAsync();
                return;
            }
            _lsp = lsp;
            lsp.Log += ServerOutput.Append;
            lsp.Progress += text => Dispatcher.UIThread.Post(() =>
            {
                if (text is not null && !lsp.IsReady)
                    ServerStatus = "IntelliSense: " + text;
            });
            lsp.ProjectsLoaded += () => Dispatcher.UIThread.Post(async () =>
            {
                ServerStatus = "IntelliSense: ready";
                // Razor discovers components in the background after the projects load (~30 s on a large Blazor
                // solution) without sending workspace/diagnostic/refresh, and early pulls report RZ10012
                // "unexpected markup element". Keep refreshing open documents while that warms up.
                for (var i = 0; i < 18; i++)
                {
                    await Task.Delay(i == 0 ? 100 : 5000);
                    if (_lsp != lsp)
                        return;
                    foreach (var d in Documents)
                        d.Host.ScheduleDiagnostics(0);
                }
            });
            lsp.DiagnosticsRefreshRequested += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var d in Documents)
                    d.Host.ScheduleDiagnostics(300);
            });
            lsp.RestoreRequested += projects => Dispatcher.UIThread.Post(() => _ = RestoreAsync(projects));
            lsp.Exited += _ => Dispatcher.UIThread.Post(() =>
            {
                if (_lsp != lsp)
                    return;
                _lsp = null;
                foreach (var d in Documents)
                    d.Host.Detach();
                ServerStatus = "IntelliSense: stopped (Tools > Restart Language Server)";
            });
            ServerStatus = "IntelliSense: loading projects...";
            foreach (var d in Documents)
                d.Host.Attach(lsp);
        }
        catch (Exception ex)
        {
            ServerOutput.Append("Failed to start language server: " + ex);
            ServerStatus = "IntelliSense: unavailable";
        }
    }

    async Task StopLanguageServerAsync()
    {
        if (_lsp is not { } lsp)
            return;
        _lsp = null;
        foreach (var d in Documents)
            d.Host.Detach();
        await lsp.DisposeAsync();
        ServerStatus = "";
    }

    [RelayCommand]
    async Task RestartLanguageServer()
    {
        await StopLanguageServerAsync();
        await StartLanguageServerAsync();
    }

    async Task RestoreAsync(IReadOnlyList<string> projects)
    {
        if (Solution is null)
            return;
        var targets = projects.Count > 0 ? projects : [Solution.FilePath];
        foreach (var target in targets)
        {
            BuildOutput.Append($"Restoring packages for {target}...");
            using var runner = ProcessRunner.Start(DotnetCli.Executable, ["restore", target], Solution.Directory, BuildOutput.Append);
            await runner.WaitAsync();
        }
    }

    // ---- Documents ---------------------------------------------------------------------------------------------

    public void OpenNode(ExplorerNode node)
    {
        if (node.Kind == NodeKind.File && node.Path is not null)
            OpenDocument(node.Path);
        else if (node.Kind == NodeKind.Project && node.Path is not null)
            OpenDocument(node.Path);
    }

    public DocumentViewModel? OpenDocument(string path, bool activate = true)
    {
        var existing = Documents.FirstOrDefault(d => d.FilePath == path);
        if (existing is null)
        {
            if (!File.Exists(path) || IsBinary(path))
                return null;
            EditorHost host;
            try
            {
                host = new EditorHost(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message);
                return null;
            }
            existing = new DocumentViewModel(host);
            Wire(existing);
            host.AttachBreakpoints(Breakpoints);
            _ = RefreshGitBaselineAsync(existing);
            if (Mode == DebugMode.Break)
                host.DataTipProvider = EvaluateDataTipAsync;
            Documents.Add(existing);
            if (_lsp is not null)
                host.Attach(_lsp);
        }
        if (activate)
            ActiveDocument = existing;
        return existing;
    }

    static bool IsBinary(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".webp" or ".xlsx" or ".docx" or ".pdf" or ".dll" or ".exe"
        or ".ttf" or ".woff" or ".woff2" or ".zip" or ".pyc";

    void Wire(DocumentViewModel doc)
    {
        var host = doc.Host;
        host.CaretMoved += () =>
        {
            if (ActiveDocument == doc)
                UpdateCaretText();
        };
        host.DiagnosticsChanged += RefreshErrorList;
        host.StatusMessage += text => SetStatus(text);
        host.DefinitionsFound += locations =>
        {
            if (locations.Count == 1)
                NavigateTo(locations[0]);
            else
                ShowFindResults("Go To Definition", locations);
        };
        host.ReferencesFound += (symbol, refs) => ShowFindResults($"'{symbol}' references", refs);
        host.WorkspaceEditRequested += ApplyWorkspaceEditAsync;
        host.WorkspaceOperationsRequested += ApplyWorkspaceOperationsAsync;
        host.Saved += () => _ = RefreshGitAsync();
        host.ZoomChanged += () => Settings.EditorZoom = host.Zoom;
        host.PromptRequested += initial => Dialogs?.PromptAsync("Rename", "New name:", initial) ?? Task.FromResult<string?>(null);
    }

    void UpdateCaretText()
    {
        if (ActiveDocument is null)
        {
            CaretText = "";
            return;
        }
        var (line, col) = ActiveDocument.Host.CaretPosition;
        CaretText = $"Ln: {line}   Ch: {col}";
    }

    [RelayCommand]
    async Task CloseDocument(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc is null)
            return;
        if (doc.IsDirty && Dialogs is not null)
        {
            var answer = await Dialogs.ConfirmSaveAsync(doc.FileName);
            if (answer is null)
                return;
            if (answer == true)
                doc.Host.Save();
        }
        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        doc.Dispose();
        if (ActiveDocument == doc || ActiveDocument is null)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        RefreshErrorList();
        SaveSession();
    }

    // ---- Document tab commands (tab context menu) -----------------------------------------------------------

    async Task CloseDocumentsAsync(IEnumerable<DocumentViewModel> docs)
    {
        foreach (var doc in docs.ToList())
        {
            var before = Documents.Count;
            await CloseDocument(doc);
            if (Documents.Count == before)
                return; // the user cancelled a save prompt
        }
    }

    [RelayCommand]
    Task CloseAllButThis(DocumentViewModel? doc) => CloseDocumentsAsync(Documents.Where(d => d != doc));

    [RelayCommand]
    Task CloseAllButPinned() => CloseDocumentsAsync(Documents.Where(d => !d.IsPinned));

    [RelayCommand]
    Task CloseAllDocuments() => CloseDocumentsAsync(Documents);

    [RelayCommand]
    Task CloseTabsToLeft(DocumentViewModel? doc) =>
        doc is null ? Task.CompletedTask : CloseDocumentsAsync(Documents.Take(Documents.IndexOf(doc)).Where(d => !d.IsPinned));

    [RelayCommand]
    Task CloseTabsToRight(DocumentViewModel? doc) =>
        doc is null ? Task.CompletedTask : CloseDocumentsAsync(Documents.Skip(Documents.IndexOf(doc) + 1).Where(d => !d.IsPinned));

    /// <summary>Pinned tabs move to the left edge, in pin order, like VS.</summary>
    [RelayCommand]
    void TogglePin(DocumentViewModel? doc)
    {
        if (doc is null)
            return;
        doc.IsPinned = !doc.IsPinned;
        var target = doc.IsPinned ? Documents.Count(d => d.IsPinned && d != doc) : Documents.Count(d => d.IsPinned);
        var active = ActiveDocument;
        Documents.Move(Documents.IndexOf(doc), Math.Min(target, Documents.Count - 1));
        ActiveDocument = active;
    }

    public void SaveDocument(DocumentViewModel doc)
    {
        try
        {
            doc.Host.Save();
            SetStatus("Item(s) Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    void Save()
    {
        if (ActiveDocument is not { } doc)
            return;
        try
        {
            doc.Host.Save();
            SetStatus("Item(s) Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    void SaveAll()
    {
        foreach (var doc in Documents.Where(d => d.IsDirty))
            doc.Host.Save();
        SetStatus("Item(s) Saved");
    }

    public void NavigateTo(LspLocation location) => NavigateTo(location.FilePath, location.Range);

    public void NavigateTo(string path, LspRange range)
    {
        var doc = OpenDocument(path);
        if (doc is null)
            return;
        Dispatcher.UIThread.Post(() => doc.Host.NavigateTo(range), DispatcherPriority.Loaded);
    }

    [RelayCommand]
    void NavigateToError(ErrorItem? item)
    {
        if (item?.FilePath is not { } path || !File.Exists(path))
            return;
        var pos = new LspPosition(Math.Max(0, item.Line - 1), Math.Max(0, item.Column - 1));
        NavigateTo(path, new LspRange(pos, pos));
    }

    [RelayCommand]
    void NavigateToFindResult(FindResultItem? item)
    {
        if (item is not null)
            NavigateTo(item.FilePath, item.Range);
    }

    /// <summary>Called when the window regains focus: pick up edits made outside VSL (git checkout etc.).</summary>
    public void CheckExternalChanges()
    {
        if (HasGit)
        {
            _ = RefreshGitAsync();
            _ = RefreshAllGitBaselinesAsync();
        }
        foreach (var doc in Documents)
            if (doc.Host.ReloadIfChangedOnDisk())
                SetStatus($"{doc.FileName} was reloaded because it changed on disk.");
    }

    async Task ApplyWorkspaceEditAsync(IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> edits)
    {
        foreach (var (path, fileEdits) in edits)
        {
            // Open each affected file so the edit is undoable and the server sees it through didChange.
            var doc = OpenDocument(path, activate: false);
            doc?.Host.ApplyEdits(fileEdits);
        }
        SetStatus($"Renamed across {edits.Count} file(s). Use Save All to write them.");
        await Task.CompletedTask;
    }

    /// <summary>Applies a code action's workspace edit: file creates/renames/deletes and text edits, in order.</summary>
    async Task ApplyWorkspaceOperationsAsync(IReadOnlyList<LspFileOperation> ops)
    {
        var filesChanged = false;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case "create":
                    if (!File.Exists(op.Path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(op.Path)!);
                        await File.WriteAllTextAsync(op.Path, "");
                        filesChanged = true;
                    }
                    break;
                case "rename" when op.NewPath is not null:
                    if (Documents.FirstOrDefault(d => d.FilePath == op.Path) is { } open)
                    {
                        if (open.IsDirty)
                            open.Host.Save();
                        Documents.Remove(open);
                        open.Dispose();
                    }
                    File.Move(op.Path, op.NewPath);
                    filesChanged = true;
                    break;
                case "delete":
                    if (Documents.FirstOrDefault(d => d.FilePath == op.Path) is { } deleted)
                    {
                        Documents.Remove(deleted);
                        deleted.Dispose();
                    }
                    File.Delete(op.Path);
                    filesChanged = true;
                    break;
                case "edit":
                    // Edits land in the editor buffer (undoable, synced to the server); the user saves as usual.
                    OpenDocument(op.Path, activate: false)?.Host.ApplyEdits(op.Edits);
                    break;
            }
        }
        if (filesChanged)
            await RefreshExplorer();
    }

    void ShowFindResults(string title, IReadOnlyList<LspLocation> locations)
    {
        FindResults.Clear();
        var lines = new Dictionary<string, string[]>();
        foreach (var loc in locations.OrderBy(l => l.FilePath, StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line))
        {
            var path = loc.FilePath;
            if (!lines.TryGetValue(path, out var text))
                lines[path] = text = File.Exists(path) ? File.ReadAllLines(path) : [];
            var line = loc.Range.Start.Line < text.Length ? text[loc.Range.Start.Line].Trim() : "";
            FindResults.Add(new FindResultItem(path, loc.Range.Start.Line + 1, loc.Range.Start.Character + 1, line, loc.Range));
        }
        FindResultsTitle = $"{title} ({locations.Count})";
        BottomTabIndex = 2;
        SetStatus($"{locations.Count} result(s) found.");
    }

    // ---- Error List ---------------------------------------------------------------------------------------

    void RefreshErrorList()
    {
        var live = Documents
            .SelectMany(d => d.Host.Diagnostics.Select(x => new ErrorItem(
                x.Severity == DiagnosticSeverity.Hint ? DiagnosticSeverity.Information : x.Severity,
                x.Code, x.Message, ProjectNameFor(d.FilePath), d.FilePath,
                x.Range.Start.Line + 1, x.Range.Start.Character + 1, "IntelliSense")));
        // VS's "Build + IntelliSense": both sources are listed; an error both report appears once.
        var all = _buildErrors.Concat(live)
            .DistinctBy(e => (e.FilePath, e.Line, e.Code, e.Description))
            .ToList();

        ErrorCount = all.Count(e => e.Severity == DiagnosticSeverity.Error);
        WarningCount = all.Count(e => e.Severity == DiagnosticSeverity.Warning);
        MessageCount = all.Count(e => e.Severity >= DiagnosticSeverity.Information);
        Errors.Clear();
        foreach (var e in all
                     .Where(e => e.Severity switch
                     {
                         DiagnosticSeverity.Error => ShowErrors,
                         DiagnosticSeverity.Warning => ShowWarnings,
                         _ => ShowMessages,
                     })
                     .OrderBy(e => e.Severity)
                     .ThenBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Line))
            Errors.Add(e);
    }

    string ProjectNameFor(string? path)
    {
        if (path is null || Solution is null)
            return "";
        return Solution.Projects
            .Where(p => path.StartsWith(p.Directory + "/", StringComparison.Ordinal))
            .OrderByDescending(p => p.Directory.Length)
            .FirstOrDefault()?.Name ?? "";
    }

    // ---- Build -------------------------------------------------------------------------------------------------

    bool CanBuild => Solution is not null && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    Task BuildSolution() => RunBuildAsync("build", "Build");

    [RelayCommand(CanExecute = nameof(CanBuild))]
    Task RebuildSolution() => RunBuildAsync("rebuild", "Rebuild All");

    [RelayCommand(CanExecute = nameof(CanBuild))]
    Task CleanSolution() => RunBuildAsync("clean", "Clean");

    [RelayCommand]
    void CancelBuild() => _buildCts?.Cancel();

    /// <summary>Runs dotnet build/clean and feeds the Output window and Error List. Returns true on success.</summary>
    async Task<bool> RunBuildAsync(string verb, string title, string? target = null)
    {
        if (Solution is not { } solution || IsBuilding)
            return false;
        SaveAll();
        IsBuilding = true;
        SelectedOutput = BuildOutput;
        BottomTabIndex = 1;
        BuildOutput.Clear();
        _buildErrors.Clear();
        RefreshErrorList();
        SetStatus($"{title} started...", StatusKind.Busy);
        var sw = Stopwatch.StartNew();
        BuildOutput.Append($"{title} started at {DateTime.Now:HH:mm}...");

        var args = new List<string> { verb == "clean" ? "clean" : "build", target ?? solution.FilePath, "-c", Configuration, "-nologo", "-v:minimal", "-clp:NoSummary;ForceNoAlign" };
        if (verb == "rebuild")
            args.Add("--no-incremental");

        var succeeded = 0;
        var cts = _buildCts = new CancellationTokenSource();
        int exitCode;
        try
        {
            using var runner = ProcessRunner.Start(DotnetCli.Executable, args, solution.Directory, line =>
            {
                BuildOutput.Append(line);
                if (line.Contains(" -> ", StringComparison.Ordinal))
                    Interlocked.Increment(ref succeeded);
                if (BuildOutputParser.Parse(line) is { } d)
                    Dispatcher.UIThread.Post(() => AddBuildError(d));
            });
            exitCode = await runner.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            BuildOutput.Append("Build was canceled.");
            exitCode = -1;
        }
        finally
        {
            IsBuilding = false;
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var errors = _buildErrors.Count(e => e.Severity == DiagnosticSeverity.Error);
        var warnings = _buildErrors.Count(e => e.Severity == DiagnosticSeverity.Warning);
        var ok = exitCode == 0;
        var verbLabel = verb == "clean" ? "Clean" : verb == "rebuild" ? "Rebuild All" : "Build";
        BuildOutput.Append($"========== {verbLabel}: {(ok ? "succeeded" : "failed")}, {errors} error(s), {warnings} warning(s), {succeeded} project(s) built ==========");
        BuildOutput.Append($"========== {verbLabel} completed at {DateTime.Now:HH:mm} and took {sw.Elapsed.TotalSeconds:0.000} seconds ==========");
        SetStatus(ok ? $"{verbLabel} succeeded" : $"{verbLabel} failed", IsRunning || IsDebugging ? StatusKind.Running : StatusKind.Ready);
        if (!ok && errors > 0)
            BottomTabIndex = 0;
        return ok;
    }

    void AddBuildError(BuildDiagnostic d)
    {
        var project = d.Project is null ? "" : Path.GetFileNameWithoutExtension(d.Project);
        var item = new ErrorItem(d.Severity, d.Code, d.Message, project, d.File is null ? null : Path.GetFullPath(d.File, Solution?.Directory ?? "/"), d.Line, d.Column, "Build");
        if (_buildErrors.Any(e => e.FilePath == item.FilePath && e.Line == item.Line && e.Code == item.Code && e.Description == item.Description))
            return;
        _buildErrors.Add(item);
        RefreshErrorList();
    }

    // ---- Run ---------------------------------------------------------------------------------------------------

    bool CanStart => Solution is not null && StartupProject is not null && !IsBuilding && !IsRunning && Mode == DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task StartWithoutDebugging()
    {
        if (Solution is not { } solution || StartupProject is not { } project)
            return;
        // Like VS: build first, run only if the build succeeded.
        if (!await RunBuildAsync("build", "Build", project.FilePath))
            return;

        var profile = project.LaunchProfiles.FirstOrDefault(p => p.Name == LaunchProfile);
        var args = new List<string> { "run", "--project", project.FilePath, "-c", Configuration, "--no-build" };
        if (profile is not null)
            args.AddRange(["--launch-profile", profile.Name]);
        else
            args.Add("--no-launch-profile");

        DebugOutput.Clear();
        SelectedOutput = DebugOutput;
        BottomTabIndex = 1;
        DebugOutput.Append($"> dotnet {string.Join(' ', args)}");
        IsRunning = true;
        SetStatus($"Running {project.Name}", StatusKind.Running);

        var browserOpened = false;
        var runner = ProcessRunner.Start(DotnetCli.Executable, args, project.Directory, line =>
        {
            DebugOutput.Append(line);
            if (!browserOpened && profile is { LaunchBrowser: true } && line.Contains("Now listening on:", StringComparison.Ordinal))
            {
                browserOpened = true;
                var url = Regex.Match(line, @"https?://\S+").Value;
                if (url.Length > 0)
                    OpenBrowser(url.TrimEnd('/') + "/" + (profile.LaunchUrl ?? ""));
            }
        });
        _runProcess = runner;
        var exit = await runner.WaitAsync();
        DebugOutput.Append($"The program '{project.Name}' has exited with code {exit} (0x{exit:x}).");
        if (_runProcess == runner)
            _runProcess = null;
        runner.Dispose();
        IsRunning = false;
        SetStatus("Ready", Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
    }

    static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception)
        {
            // No desktop opener available; the URL is in the Output window anyway.
        }
    }

    // ---- Solution Explorer helpers -------------------------------------------------------------------------

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var root in ExplorerRoots)
            Collapse(root);
        if (ExplorerRoots.FirstOrDefault() is { } r)
            r.IsExpanded = true;

        static void Collapse(ExplorerNode node)
        {
            node.IsExpanded = false;
            foreach (var c in node.Children)
                Collapse(c);
        }
    }

    [RelayCommand]
    async Task RefreshExplorer()
    {
        if (Solution is null)
            return;
        var reloaded = await SolutionLoader.LoadAsync(Solution.FilePath);
        reloaded.Root.RefreshGitBadges();
        ExplorerRoots.Clear();
        ExplorerRoots.Add(reloaded.Root);
    }

    /// <summary>Selects the active document in Solution Explorer (VS: "Sync with Active Document").</summary>
    public ExplorerNode? SyncWithActiveDocument()
    {
        if (ActiveDocument is null || ExplorerRoots.FirstOrDefault() is not { } root)
            return null;
        var node = root.FindByPath(ActiveDocument.FilePath);
        if (node is null)
            return null;
        ExpandAncestors(root, node);
        return node;
    }

    static bool ExpandAncestors(ExplorerNode current, ExplorerNode target)
    {
        if (current == target)
            return true;
        foreach (var child in current.Children)
        {
            if (ExpandAncestors(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>Find in Files (Ctrl+Shift+F): plain case-insensitive search over the solution's source files.</summary>
    public async Task FindInFilesAsync(string text)
    {
        if (Solution is not { } solution)
            return;
        SetStatus($"Find all \"{text}\"...", StatusKind.Busy);
        var hits = await Task.Run(() =>
        {
            var results = new List<FindResultItem>();
            foreach (var file in Directory.EnumerateFiles(solution.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal)
                    || file.Contains("/.", StringComparison.Ordinal) || file.Contains("/node_modules/", StringComparison.Ordinal)
                    || IsBinary(file) || new FileInfo(file).Length > 2_000_000)
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length && results.Count < 5000; i++)
                {
                    var col = lines[i].IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (col < 0)
                        continue;
                    var start = new LspPosition(i, col);
                    results.Add(new FindResultItem(file, i + 1, col + 1, lines[i].Trim(), new LspRange(start, start with { Character = col + text.Length })));
                }
            }
            return results;
        });
        FindResults.Clear();
        foreach (var hit in hits)
            FindResults.Add(hit);
        FindResultsTitle = $"Find All \"{text}\" ({hits.Count})";
        BottomTabIndex = 2;
        SetStatus($"Matching lines: {hits.Count}", StatusKind.Ready);
    }

    /// <summary>Files whose name contains the filter, for the Solution Explorer search box.</summary>
    public IEnumerable<ExplorerNode> SearchFiles(string filter)
    {
        if (Solution is null || filter.Length < 2)
            yield break;
        foreach (var project in Solution.Projects)
        {
            if (!Directory.Exists(project.Directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(project.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal) || file.Contains("/.", StringComparison.Ordinal))
                    continue;
                var name = Path.GetFileName(file);
                if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    yield return new ExplorerNode(NodeKind.File, name, file) { Detail = Path.GetRelativePath(Solution.Directory, Path.GetDirectoryName(file)!) };
            }
        }
    }
}
