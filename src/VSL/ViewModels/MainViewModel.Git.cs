using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Git;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>A row in the Git Changes window.</summary>
public sealed record GitFileItem(GitFileChange Change)
{
    public string FileName => Path.GetFileName(Change.Path);

    public string Folder => Path.GetDirectoryName(Change.RelativePath) is { Length: > 0 } dir ? dir : "";

    /// <summary>The folder shown after the file name, gray, as in VS.</summary>
    public string FolderSuffix => Folder.Length > 0 ? "  " + Folder : "";

    public string Letter => Change.Letter;

    public string LetterColor => Change.Kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked or GitChangeKind.Renamed => "#73C991",
        GitChangeKind.Deleted => "#C74E39",
        GitChangeKind.Conflicted => "#E4676B",
        _ => "#E2C08D",
    };
}

/// <summary>Git integration: Git Changes window, branch status bar, Solution Explorer badges, editor change margin.</summary>
public partial class MainViewModel
{
    GitRepository? _git;
    GitWatcher? _gitWatcher;
    GitStatus? _gitStatus;
    Dictionary<string, string> _gitBadges = [];
    bool _gitRefreshing;
    bool _gitRefreshQueued;

    public OutputChannel GitOutput { get; } = new("Source Control - Git");
    public ObservableCollection<GitFileItem> StagedChanges { get; } = [];
    public ObservableCollection<GitFileItem> UnstagedChanges { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand), nameof(SyncCommand), nameof(NewBranchCommand))]
    public partial bool HasGit { get; set; }

    [ObservableProperty]
    public partial string GitBranch { get; set; } = "";

    [ObservableProperty]
    public partial string GitSyncText { get; set; } = "";

    [ObservableProperty]
    public partial int GitChangeCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand), nameof(SyncCommand), nameof(NewBranchCommand))]
    public partial bool IsGitBusy { get; set; }

    /// <summary>"Commit All" when nothing is staged, "Commit Staged" otherwise (like VS).</summary>
    public string CommitButtonText => StagedChanges.Count > 0 ? "Commit Staged" : "Commit All";

    async Task OpenGitAsync(SolutionInfo solution)
    {
        _git = await GitRepository.OpenAsync(solution.Directory);
        HasGit = _git is not null;
        ExplorerNode.GitStatusProvider = path => _gitBadges.GetValueOrDefault(path);
        if (_git is not { } git)
            return;
        await RefreshGitAsync();
        try
        {
            var watcher = new GitWatcher(solution.Directory, git.GitDir, TimeSpan.FromMilliseconds(400));
            watcher.Changed += (repository, files) => Dispatcher.UIThread.Post(() => _ = OnRepositoryChangedAsync(watcher, repository, files));
            _gitWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GitOutput.Append("Not watching the repository for outside changes: " + ex.Message);
        }
    }

    /// <summary>A commit, checkout or edit made outside VSL (e.g. in the terminal): follow it right away.</summary>
    async Task OnRepositoryChangedAsync(GitWatcher watcher, bool repository, IReadOnlyCollection<string> files)
    {
        if (_gitWatcher != watcher)
            return;
        await RefreshGitAsync();
        foreach (var doc in Editors.Where(d => files.Contains(d.FilePath)).ToList())
            if (doc.Host.ReloadIfChangedOnDisk())
                SetStatus($"{doc.FileName} was reloaded because it changed on disk.");
        if (repository)
            await RefreshAllGitBaselinesAsync();
    }

    void CloseGit()
    {
        _gitWatcher?.Dispose();
        _gitWatcher = null;
        _git = null;
        _gitStatus = null;
        _gitBadges = [];
        HasGit = false;
        GitBranch = GitSyncText = "";
        GitChangeCount = 0;
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        Branches.Clear();
        CommitMessage = "";
    }

    /// <summary>Re-reads status; coalesces bursts (saves, focus changes) into at most one extra run.</summary>
    public async Task RefreshGitAsync()
    {
        if (_git is not { } git)
            return;
        if (_gitRefreshing)
        {
            _gitRefreshQueued = true;
            return;
        }
        _gitRefreshing = true;
        try
        {
            do
            {
                _gitRefreshQueued = false;
                var status = await git.StatusAsync();
                if (_git != git)
                    return;
                ApplyGitStatus(status);
                var branches = await git.BranchesAsync();
                Branches.Clear();
                foreach (var b in branches)
                    Branches.Add(b);
            }
            while (_gitRefreshQueued);
        }
        catch (Exception ex)
        {
            GitOutput.Append("git status failed: " + ex.Message);
        }
        finally
        {
            _gitRefreshing = false;
        }
    }

    void ApplyGitStatus(GitStatus status)
    {
        _gitStatus = status;
        GitBranch = status.Branch.Branch ?? "(detached HEAD)";
        GitSyncText = status.Branch.Upstream is null ? "" : $"↑{status.Branch.Ahead} ↓{status.Branch.Behind}";
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        foreach (var c in status.Changes.OrderBy(c => c.RelativePath, StringComparer.Ordinal))
            (c.Staged ? StagedChanges : UnstagedChanges).Add(new GitFileItem(c));
        GitChangeCount = status.Changes.Select(c => c.Path).Distinct().Count();
        OnPropertyChanged(nameof(CommitButtonText));
        CommitCommand.NotifyCanExecuteChanged();

        // Working-tree status wins over the index for the badge, matching what the editor shows.
        _gitBadges = status.Changes.OrderBy(c => c.Staged ? 0 : 1).GroupBy(c => c.Path).ToDictionary(g => g.Key, g => g.Last().Letter);
        foreach (var root in ExplorerRoots)
            root.RefreshGitBadges();
    }

    /// <summary>Points each open editor's change margin at the file's HEAD content.</summary>
    async Task RefreshGitBaselineAsync(DocumentViewModel doc)
    {
        if (_git is not { } git || !doc.FilePath.StartsWith(git.Root + "/", StringComparison.Ordinal))
        {
            doc.Host.SetGitBaseline(null);
            return;
        }
        var head = await git.ShowHeadAsync(doc.FilePath);
        var tracked = _gitStatus?.Changes.Any(c => c.Path == doc.FilePath && c.Kind == GitChangeKind.Untracked) != true;
        doc.Host.SetGitBaseline(head ?? (tracked ? null : ""));
    }

    async Task RefreshAllGitBaselinesAsync()
    {
        foreach (var doc in Editors.ToList())
            await RefreshGitBaselineAsync(doc);
    }

    // ---- Commands ------------------------------------------------------------------------------------------

    bool CanCommit => HasGit && !IsGitBusy && !string.IsNullOrWhiteSpace(CommitMessage) && GitChangeCount > 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    async Task Commit()
    {
        if (_git is not { } git)
            return;
        SaveAll();
        await GitOperationAsync("Commit", () => git.CommitAsync(CommitMessage.Trim(), all: StagedChanges.Count == 0), success: () => CommitMessage = "");
        await RefreshAllGitBaselinesAsync();
    }

    [RelayCommand]
    async Task Stage(GitFileItem? item)
    {
        if (_git is { } git && item is not null)
            await GitOperationAsync(null, () => git.StageAsync([item.Change.Path]));
    }

    [RelayCommand]
    async Task Unstage(GitFileItem? item)
    {
        if (_git is { } git && item is not null)
            await GitOperationAsync(null, () => git.UnstageAsync([item.Change.Path]));
    }

    [RelayCommand]
    async Task StageAll()
    {
        if (_git is { } git && UnstagedChanges.Count > 0)
            await GitOperationAsync(null, () => git.StageAsync(UnstagedChanges.Select(c => c.Change.Path).Distinct().ToList()));
    }

    [RelayCommand]
    async Task UnstageAll()
    {
        if (_git is { } git && StagedChanges.Count > 0)
            await GitOperationAsync(null, () => git.UnstageAsync(StagedChanges.Select(c => c.Change.Path).Distinct().ToList()));
    }

    [RelayCommand]
    async Task Discard(GitFileItem? item)
    {
        if (_git is not { } git || item is null)
            return;
        if (Dialogs is not null && await Dialogs.ConfirmAsync("Undo Changes", $"Undo the changes to {item.FileName}? This cannot be undone.") != true)
            return;
        await GitOperationAsync(null, () => git.DiscardAsync(item.Change));
        CheckExternalChanges();
        await RefreshAllGitBaselinesAsync();
        if (item.Change.Kind == GitChangeKind.Untracked)
            await RefreshExplorer();
    }

    bool CanSync => HasGit && !IsGitBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task Fetch() => _git is { } git ? GitOperationAsync("Fetch", () => git.FetchAsync(GitOutput.Append)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task Pull()
    {
        if (_git is not { } git)
            return;
        SaveAll();
        await GitOperationAsync("Pull", () => git.PullAsync(GitOutput.Append));
        await AfterWorkingTreeChangedAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task Push() => _git is { } git && _gitStatus is { } status
        ? GitOperationAsync("Push", () => git.PushAsync(status.Branch, GitOutput.Append))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task Sync()
    {
        if (_git is not { } git)
            return;
        SaveAll();
        if (await GitOperationAsync("Pull", () => git.PullAsync(GitOutput.Append)) && _gitStatus is { } status)
            await GitOperationAsync("Push", () => git.PushAsync(status.Branch, GitOutput.Append));
        await AfterWorkingTreeChangedAsync();
    }

    [RelayCommand]
    async Task SwitchBranch(string? branch)
    {
        if (_git is not { } git || string.IsNullOrEmpty(branch) || branch == GitBranch)
            return;
        SaveAll();
        await GitOperationAsync($"Checkout {branch}", () => git.SwitchAsync(branch));
        await AfterWorkingTreeChangedAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task NewBranch()
    {
        if (Dialogs is null || await Dialogs.PromptAsync("Create a New Branch", "Branch name:", "") is not { Length: > 0 } name)
            return;
        await CreateBranchAsync(name.Trim());
    }

    public async Task CreateBranchAsync(string name)
    {
        if (_git is { } git)
            await GitOperationAsync($"Create branch {name}", () => git.CreateBranchAsync(name));
    }

    [RelayCommand]
    async Task OpenDiff(GitFileItem? item)
    {
        if (_git is not { } git || item is null)
            return;
        var change = item.Change;
        string left, right, leftLabel, rightLabel, key;
        if (change.Staged)
        {
            // Staged change: what the commit would record (index) against HEAD.
            left = await git.ShowHeadAsync(change.Path) ?? "";
            right = await git.ShowIndexAsync(change.Path) ?? "";
            (leftLabel, rightLabel, key) = ($"{item.FileName};HEAD", $"{item.FileName};Staged", "staged");
        }
        else
        {
            // Working-tree change: the file on disk against the index (HEAD when nothing is staged).
            left = change.Kind == GitChangeKind.Untracked ? "" : await git.ShowIndexAsync(change.Path) ?? await git.ShowHeadAsync(change.Path) ?? "";
            right = change.Kind == GitChangeKind.Deleted || !File.Exists(change.Path) ? "" : await File.ReadAllTextAsync(change.Path);
            (leftLabel, rightLabel, key) = ($"{item.FileName};Index", $"{item.FileName};Working Tree", "working");
        }
        ShowDiff(new DiffViewModel(change.Path, left, right, leftLabel, rightLabel, key));
    }

    /// <summary>Compare with Unmodified: the editor's current text against HEAD.</summary>
    public async Task CompareWithUnmodifiedAsync(DocumentViewModel doc)
    {
        if (_git is not { } git)
            return;
        var head = await git.ShowHeadAsync(doc.FilePath) ?? "";
        ShowDiff(new DiffViewModel(doc.FilePath, head, doc.Host.Document.Text, $"{doc.FileName};HEAD", $"{doc.FileName};Current", "head"));
    }

    void ShowDiff(DiffViewModel diff)
    {
        // Reopen so the tab shows the latest content instead of a stale comparison.
        if (Documents.FirstOrDefault(d => d.Key == diff.Key) is { } stale)
        {
            Documents.Remove(stale);
            stale.Dispose();
        }
        Documents.Add(diff);
        ActiveDocument = diff;
    }

    [RelayCommand]
    Task CompareActiveWithUnmodified() => ActiveEditor is { } doc ? CompareWithUnmodifiedAsync(doc) : Task.CompletedTask;

    [RelayCommand]
    void ShowGitChanges() => ShowToolWindow(ToolWindows.GitChanges);

    /// <summary>Runs a git command, logs it to the Git output pane and refreshes status. Returns success.</summary>
    async Task<bool> GitOperationAsync(string? title, Func<Task<GitResult>> operation, Action? success = null)
    {
        IsGitBusy = true;
        try
        {
            if (title is not null)
            {
                SetStatus($"Git: {title}...", StatusKind.Busy);
                GitOutput.Append($"> {title}");
            }
            var result = await operation();
            if (!string.IsNullOrWhiteSpace(result.Output) && (title is not null || !result.Success))
                GitOutput.Append(result.Output.TrimEnd());
            if (result.Success)
            {
                success?.Invoke();
                if (title is not null)
                    SetStatus($"Git: {title} succeeded", StatusKind.Ready);
            }
            else
            {
                SelectedOutput = GitOutput;
                ShowToolWindow(ToolWindows.Output);
                SetStatus($"Git: {title ?? "operation"} failed (see Output)", StatusKind.Ready);
            }
            return result.Success;
        }
        catch (Exception ex)
        {
            GitOutput.Append(ex.Message);
            SetStatus("Git: " + ex.Message, StatusKind.Ready);
            return false;
        }
        finally
        {
            IsGitBusy = false;
            await RefreshGitAsync();
        }
    }

    /// <summary>After pull/checkout the files on disk changed: reload editors, margins and the tree.</summary>
    async Task AfterWorkingTreeChangedAsync()
    {
        CheckExternalChanges();
        await RefreshAllGitBaselinesAsync();
        await RefreshExplorer();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
