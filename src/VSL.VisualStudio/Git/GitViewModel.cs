using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.ViewModels;

namespace VSL.VisualStudio.Git;

/// <summary>A git repository of the solution: its folder, branch, how far it is from its upstream, and its changes.</summary>
public sealed partial class GitRepo(string root) : ObservableObject
{
    public string Root { get; } = root;

    public string Name { get; } = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : root;

    [ObservableProperty]
    public partial string Branch { get; set; } = "HEAD";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AheadText))]
    public partial int Ahead { get; set; }

    [ObservableProperty]
    public partial int Behind { get; set; }

    /// <summary>Commits waiting to be pushed, for the repository menu ("" when none).</summary>
    public string AheadText => Ahead > 0 ? Ahead.ToString() : "";

    /// <summary>The scope chosen in the repository menu: this one alone.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public List<GitChange> Changes { get; set; } = [];

    public bool HasStaged => Changes.Any(c => c.IsStaged);
}

/// <summary>A branch in the branch menu: local ("main") or remote ("origin/main"), and in how many repositories in scope.</summary>
public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, int Repositories)
{
    /// <summary>Highlights the current branch in the menu.</summary>
    public bool IsSelected => IsCurrent;
}

/// <summary>
/// One row of the changes list: a file of a repository, staged or not. A file changed in both the index and the
/// working tree (git status "MM") is two rows, one in each section, as in Git Changes.
/// </summary>
public sealed record GitChange(GitRepo Repo, string Code, string Path, string? OriginalPath, bool IsStaged)
{
    /// <summary>The column of the status code this row stands for: the index for staged rows, the working tree otherwise.</summary>
    char State => Code == "??" ? 'A' : IsStaged ? Code[0] : Code[1];

    /// <summary>One letter for the row: M(odified), A(dded, also untracked), D(eleted), R(enamed), U (conflict).</summary>
    public string Letter => Code.Contains('U') || Code is "AA" or "DD" ? "U" : State switch
    {
        'A' or 'C' => "A",
        'D' => "D",
        'R' => "R",
        _ => "M",
    };

    public string Kind => Letter switch
    {
        "A" => "Added",
        "D" => "Deleted",
        "R" => "Renamed",
        "U" => "Conflict",
        _ => "Modified",
    };

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The folder part of the path, shown dimmed after the file name.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

/// <summary>
/// The commit panel, after Visual Studio's Git Changes: every repository of the solution (the one it is in and those
/// in its folders), a scope (all of them or one), staged and unstaged changes, a commit message Claude can write from
/// the diff, and commit, fetch, pull, push and sync. Every git command runs as the user, with their name and email
/// from git config and their credentials; the message gets no Claude attribution or trailers.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    /// <summary>The diff Claude reads is cut here; the file list still names every change.</summary>
    const int MaxDiffChars = 60_000;

    /// <summary>How deep the solution's folders are searched for repositories.</summary>
    const int SearchDepth = 3;

    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages" };

    readonly Func<string> _workingDirectory;
    readonly Func<IReadOnlyList<string>> _projectDirectories;
    readonly Func<ClaudeViewModel> _claude;

    public GitViewModel(Func<string> workingDirectory, Func<IReadOnlyList<string>> projectDirectories, Func<ClaudeViewModel> claude)
    {
        _workingDirectory = workingDirectory;
        _projectDirectories = projectDirectories;
        _claude = claude;
    }

    public ObservableCollection<GitRepo> Repositories { get; } = [];

    /// <summary>The repositories the repository menu lists, narrowed by its filter.</summary>
    public ObservableCollection<GitRepo> FilteredRepositories { get; } = [];

    public ObservableCollection<GitChange> StagedChanges { get; } = [];

    public ObservableCollection<GitChange> UnstagedChanges { get; } = [];

    /// <summary>The visible rows of the staged tree and of the changes tree.</summary>
    public ObservableCollection<GitTreeNode> StagedRows { get; } = [];

    public ObservableCollection<GitTreeNode> UnstagedRows { get; } = [];

    List<GitTreeNode> _stagedTree = [];
    List<GitTreeNode> _unstagedTree = [];

    /// <summary>The folders the user collapsed (everything starts expanded, as in Git Changes).</summary>
    readonly HashSet<string> _collapsed = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StagedChevron))]
    public partial bool IsStagedSectionExpanded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangesChevron))]
    public partial bool IsChangesSectionExpanded { get; set; } = true;

    public string StagedChevron => IsStagedSectionExpanded ? "Chevron" : "ChevronRight";

    public string ChangesChevron => IsChangesSectionExpanded ? "Chevron" : "ChevronRight";

    /// <summary>Branches of the repositories in scope, for the branch menu (narrowed by its filter).</summary>
    public ObservableCollection<GitBranch> LocalBranches { get; } = [];

    public ObservableCollection<GitBranch> RemoteBranches { get; } = [];

    List<GitBranch> _branches = [];

    [ObservableProperty]
    public partial bool IsBranchMenuOpen { get; set; }

    [ObservableProperty]
    public partial string BranchFilter { get; set; } = "";

    partial void OnBranchFilterChanged(string value) => FilterBranches();

    /// <summary>The name typed for a new branch.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand))]
    public partial string NewBranchName { get; set; } = "";

    /// <summary>A file to open in the editor (double-click on a row).</summary>
    public event Action<string>? FileOpenRequested;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>The one repository the panel works on; null for all of them.</summary>
    [ObservableProperty]
    public partial GitRepo? SelectedRepository { get; set; }

    [ObservableProperty]
    public partial bool IsRepositoryMenuOpen { get; set; }

    [ObservableProperty]
    public partial string RepositoryFilter { get; set; } = "";

    partial void OnRepositoryFilterChanged(string value) => Filter();

    public bool IsRepository => Repositories.Count > 0;

    public bool IsMultiRepository => Repositories.Count > 1;

    /// <summary>Why no repository was found: the folder that was searched and git's own words.</summary>
    [ObservableProperty]
    public partial string? RepositoryProblem { get; set; }

    /// <summary>"Name &lt;email&gt;" from git config: who the commits are made as.</summary>
    [ObservableProperty]
    public partial string? Author { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(GenerateCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(SyncCommand), nameof(StageCommand), nameof(UnstageCommand), nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(CreateBranchCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What is happening ("Writing the message…") or what just happened ("Committed a1b2c3d").</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The repositories the commands act on: the selected one, or all.</summary>
    IReadOnlyList<GitRepo> Scope => SelectedRepository is { } one ? [one] : Repositories;

    /// <summary>"5 Repositories", or the name of the one in scope.</summary>
    public string RepositoryLabel => SelectedRepository?.Name ?? (Repositories.Count == 1 ? Repositories[0].Name : $"{Repositories.Count} Repositories");

    public string AllRepositoriesLabel => $"All Repositories ({Repositories.Count})";

    /// <summary>The branch of the repositories in scope, or "[multiple]" when they differ.</summary>
    public string BranchLabel => Scope.Select(r => r.Branch).Distinct().ToList() is { Count: 1 } one ? one[0] : "[multiple]";

    /// <summary>Outgoing / incoming commits of the repositories in scope, as Git Changes shows them.</summary>
    public string SyncLabel => $"{Scope.Sum(r => r.Ahead)} / {Scope.Sum(r => r.Behind)}";

    public bool IsAllSelected => SelectedRepository is null;

    /// <summary>Staged changes commit alone, as in Git Changes; without any, every change is committed.</summary>
    bool CommitsStaged => StagedChanges.Count > 0;

    public string CommitLabel => CommitsStaged ? "Commit Staged"
        : Scope.Count(r => r.Changes.Count > 0) > 1 ? "Commit All Repos"
        : "Commit All";

    public bool HasStagedChanges => StagedChanges.Count > 0;

    public bool HasNoChanges => IsRepository && StagedChanges.Count == 0 && UnstagedChanges.Count == 0;

    [RelayCommand]
    async Task ToggleAsync()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
            await RefreshAsync();
    }

    [RelayCommand]
    void Close()
    {
        IsOpen = false;
        IsRepositoryMenuOpen = false;
    }

    [RelayCommand]
    void ToggleRepositoryMenu()
    {
        IsBranchMenuOpen = false;
        IsRepositoryMenuOpen = !IsRepositoryMenuOpen;
        RepositoryFilter = "";
        Filter();
    }

    [RelayCommand]
    void CloseRepositoryMenu() => IsRepositoryMenuOpen = false;

    /// <summary>Opens the branch menu with the branches of the repositories in scope.</summary>
    [RelayCommand]
    async Task ToggleBranchMenuAsync()
    {
        IsRepositoryMenuOpen = false;
        if (IsBranchMenuOpen)
        {
            IsBranchMenuOpen = false;
            return;
        }
        BranchFilter = "";
        NewBranchName = "";
        IsBranchMenuOpen = true;
        var scope = Scope.ToList();
        var lists = await Task.WhenAll(scope.Select(repo =>
            GitProcess.RunGitAsync(repo.Root, "for-each-ref --format=%(refname) refs/heads refs/remotes")));
        var branches = new Dictionary<string, (bool Remote, bool Current, int Count)>(StringComparer.Ordinal);
        for (var i = 0; i < scope.Count; i++)
        {
            foreach (var line in lists[i].Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                var remote = line.StartsWith("refs/remotes/", StringComparison.Ordinal);
                var name = line.Substring(remote ? "refs/remotes/".Length : "refs/heads/".Length);
                if (remote && name.EndsWith("/HEAD", StringComparison.Ordinal))
                    continue;
                var current = !remote && name == scope[i].Branch;
                branches[(remote ? "r:" : "l:") + name] = branches.TryGetValue((remote ? "r:" : "l:") + name, out var known)
                    ? (remote, known.Current || current, known.Count + 1)
                    : (remote, current, 1);
            }
        }
        _branches = branches.Select(b => new GitBranch(b.Key.Substring(2), b.Value.Remote, b.Value.Current, b.Value.Count))
            .OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        FilterBranches();
    }

    [RelayCommand]
    void CloseBranchMenu() => IsBranchMenuOpen = false;

    void FilterBranches()
    {
        LocalBranches.Clear();
        RemoteBranches.Clear();
        var filter = BranchFilter.Trim();
        foreach (var branch in _branches.Where(b => filter.Length == 0 || b.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
            (branch.IsRemote ? RemoteBranches : LocalBranches).Add(branch);
    }

    /// <summary>
    /// git switch in every repository in scope that has the branch. A remote branch ("origin/feature") switches to its
    /// local branch, made to track it when there is none yet, as Git Changes does.
    /// </summary>
    [RelayCommand]
    async Task CheckoutBranchAsync(GitBranch? branch)
    {
        if (branch is null || IsBusy)
            return;
        IsBranchMenuOpen = false;
        var local = branch.IsRemote ? branch.Name.Substring(branch.Name.IndexOf('/') + 1) : branch.Name;
        var done = await RunAsync($"Switching to {local}…", Scope, async repo =>
        {
            var hasLocal = (await GitProcess.RunGitAsync(repo.Root, $"rev-parse --verify --quiet refs/heads/{local}")).Ok;
            if (hasLocal)
                return await GitProcess.RunGitAsync(repo.Root, $"switch {Quote(local)}");
            if (branch.IsRemote && (await GitProcess.RunGitAsync(repo.Root, $"rev-parse --verify --quiet refs/remotes/{branch.Name}")).Ok)
                return await GitProcess.RunGitAsync(repo.Root, $"switch -c {Quote(local)} --track {Quote(branch.Name)}");
            return new ProcessResult(1, "", $"There is no branch {branch.Name}");
        });
        if (done > 0)
            Status = $"Switched to {local}";
    }

    bool CanCreateBranch => !IsBusy && NewBranchName.Trim().Length > 0;

    /// <summary>git switch -c: a new branch from the current commit, in every repository in scope.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim().Replace(' ', '-');
        IsBranchMenuOpen = false;
        NewBranchName = "";
        if (await RunAsync($"Creating {name}…", Scope, repo => GitProcess.RunGitAsync(repo.Root, $"switch -c {Quote(name)}")) > 0)
            Status = $"Switched to {name}";
    }

    /// <summary>Picks the scope: a repository, or null for all of them.</summary>
    [RelayCommand]
    void SelectRepository(GitRepo? repository)
    {
        SelectedRepository = repository;
        foreach (var repo in Repositories)
            repo.IsSelected = repo == repository;
        IsRepositoryMenuOpen = false;
        ShowChanges();
    }

    void Filter()
    {
        FilteredRepositories.Clear();
        foreach (var repo in Repositories.Where(r => RepositoryFilter.Trim().Length == 0 || r.Name.IndexOf(RepositoryFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
            FilteredRepositories.Add(repo);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var directory = _workingDirectory();
        var roots = await FindRepositoriesAsync(directory, _projectDirectories());
        if (roots.Count == 0)
        {
            var top = await GitProcess.RunGitAsync(directory, "rev-parse --show-toplevel");
            RepositoryProblem = $"{directory}\n{top.Problem}".TrimEnd();
            Repositories.Clear();
            SelectedRepository = null;
            Status = null;
            ShowChanges();
            return;
        }
        RepositoryProblem = null;

        // Keep the repository objects (and the chosen scope) across refreshes; drop the ones that are gone.
        var known = Repositories.ToDictionary(r => r.Root, StringComparer.OrdinalIgnoreCase);
        var repositories = roots.Select(root => known.TryGetValue(root, out var repo) ? repo : new GitRepo(root)).ToList();
        await Task.WhenAll(repositories.Select(ReadAsync));
        Repositories.Clear();
        foreach (var repo in repositories)
            Repositories.Add(repo);
        if (SelectedRepository is { } selected && !repositories.Contains(selected))
            SelectedRepository = null;
        if (Repositories.Count == 1)
            SelectedRepository = null;

        var first = SelectedRepository ?? Repositories[0];
        var name = (await GitProcess.RunGitAsync(first.Root, "config user.name")).Output.Trim();
        var email = (await GitProcess.RunGitAsync(first.Root, "config user.email")).Output.Trim();
        Author = name.Length > 0 ? (email.Length > 0 ? $"{name} <{email}>" : name) : null;
        Filter();
        ShowChanges();
    }

    /// <summary>
    /// Every repository of the solution, as in Git Changes: the one its folder is in, those inside that folder, and
    /// the ones its projects are in (projects linked from folders outside the solution's).
    /// </summary>
    static async Task<List<string>> FindRepositoriesAsync(string directory, IReadOnlyList<string> projectDirectories)
    {
        var roots = new List<string>();
        if (await TopLevelAsync(directory) is { } own)
            roots.Add(own);
        var others = new List<string>();
        var nested = await Task.Run(() =>
        {
            var found = new List<string>();
            Search(directory, 0, found);
            return found;
        });
        others.AddRange(nested);
        var projectRoots = await Task.WhenAll(projectDirectories.Select(TopLevelAsync));
        others.AddRange(projectRoots.OfType<string>());
        foreach (var root in others.OrderBy(r => Path.GetFileName(r), StringComparer.OrdinalIgnoreCase))
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                roots.Add(root);
        return roots;
    }

    /// <summary>The root of the repository the folder is in, or null.</summary>
    static async Task<string?> TopLevelAsync(string directory)
    {
        var top = await GitProcess.RunGitAsync(directory, "rev-parse --show-toplevel");
        return top.Ok && top.Output.Trim() is { Length: > 0 } path
            ? Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar)
            : null;
    }

    static void Search(string directory, int depth, List<string> found)
    {
        if (depth >= SearchDepth)
            return;
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (SkippedFolders.Contains(name) || name.StartsWith(".", StringComparison.Ordinal))
                continue;
            // A .git folder, or a .git file (a submodule or a worktree).
            var git = Path.Combine(child, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                found.Add(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar));
            Search(child, depth + 1, found);
        }
    }

    /// <summary>Branch, upstream distance and changes of one repository (git runs off the UI thread).</summary>
    static async Task ReadAsync(GitRepo repo)
    {
        var branchRun = GitProcess.RunGitAsync(repo.Root, "branch --show-current");
        var statusRun = GitProcess.RunGitAsync(repo.Root, "status --porcelain=v1 --untracked-files=all");
        var countsRun = GitProcess.RunGitAsync(repo.Root, "rev-list --left-right --count @{u}...HEAD");
        var branch = await branchRun;
        var status = await statusRun;
        var counts = await countsRun;

        repo.Branch = branch.Output.Trim() is { Length: > 0 } current ? current : "HEAD";
        var parts = counts.Ok ? counts.Output.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries) : [];
        repo.Behind = parts.Length == 2 && int.TryParse(parts[0], out var behind) ? behind : 0;
        repo.Ahead = parts.Length == 2 && int.TryParse(parts[1], out var ahead) ? ahead : 0;

        var changes = new List<GitChange>();
        foreach (var line in status.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3))
        {
            var code = line.Substring(0, 2);
            var path = line.Substring(3);
            string? original = null;
            if (path.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and >= 0)
            {
                original = path.Substring(0, arrow).Trim('"');
                path = path.Substring(arrow + 4);
            }
            path = path.Trim('"');
            if (code[0] is not ' ' and not '?')
                changes.Add(new GitChange(repo, code, path, original, true));
            if (code[1] is not ' ' || code == "??")
                changes.Add(new GitChange(repo, code, path, null, false));
        }
        repo.Changes = changes;
    }

    /// <summary>Fills the two sections from the repositories in scope and updates everything that depends on them.</summary>
    void ShowChanges()
    {
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        foreach (var change in Scope.SelectMany(r => r.Changes))
            (change.IsStaged ? StagedChanges : UnstagedChanges).Add(change);
        _stagedTree = GitTreeNode.Build(StagedChanges, true, IsMultiRepository && Scope.Count > 1);
        _unstagedTree = GitTreeNode.Build(UnstagedChanges, false, IsMultiRepository && Scope.Count > 1);
        foreach (var node in GitTreeNode.All(_stagedTree.Concat(_unstagedTree)))
            node.IsExpanded = !_collapsed.Contains(node.Key);
        ShowRows();
        foreach (var name in new[] { nameof(IsRepository), nameof(IsMultiRepository), nameof(RepositoryLabel), nameof(AllRepositoriesLabel), nameof(BranchLabel),
                     nameof(SyncLabel), nameof(IsAllSelected), nameof(CommitLabel), nameof(HasStagedChanges), nameof(HasNoChanges) })
            OnPropertyChanged(name);
        CommitCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        StageAllCommand.NotifyCanExecuteChanged();
        UnstageAllCommand.NotifyCanExecuteChanged();
        FetchCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        SyncCommand.NotifyCanExecuteChanged();
    }

    void ShowRows()
    {
        StagedRows.Clear();
        UnstagedRows.Clear();
        foreach (var row in GitTreeNode.Visible(_stagedTree))
            StagedRows.Add(row);
        foreach (var row in GitTreeNode.Visible(_unstagedTree))
            UnstagedRows.Add(row);
    }

    /// <summary>Expands or collapses a folder, repository or the root.</summary>
    [RelayCommand]
    void ToggleNode(GitTreeNode? node)
    {
        if (node is not { IsExpandable: true })
            return;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
            _collapsed.Remove(node.Key);
        else
            _collapsed.Add(node.Key);
        ShowRows();
    }

    /// <summary>"staged" or "changes": shows or hides that section.</summary>
    [RelayCommand]
    void ToggleSection(string? section)
    {
        if (section == "staged")
            IsStagedSectionExpanded = !IsStagedSectionExpanded;
        else
            IsChangesSectionExpanded = !IsChangesSectionExpanded;
    }

    /// <summary>A click on a row: a folder opens or closes, a file opens in the editor.</summary>
    [RelayCommand]
    void ActivateNode(GitTreeNode? node)
    {
        if (node is { IsExpandable: true })
            ToggleNode(node);
        else
            OpenNode(node);
    }

    void OpenNode(GitTreeNode? node)
    {
        if (node is { Kind: GitNodeKind.File, Repo: { } repo } && Path.Combine(repo.Root, node.Path.Replace('/', Path.DirectorySeparatorChar)) is var path && File.Exists(path))
            FileOpenRequested?.Invoke(path);
    }

    /// <summary>What a row stands for in git: each repository under it, with the path inside it (null for all of it).</summary>
    static IEnumerable<(GitRepo Repo, string? Path)> Targets(GitTreeNode node) => node.Kind switch
    {
        GitNodeKind.Root => node.Children.Select(c => (c.Repo!, (string?)null)),
        GitNodeKind.Repository => [(node.Repo!, null)],
        _ => [(node.Repo!, node.Path)],
    };

    bool CanStage(GitTreeNode? node) => !IsBusy && node is not null;

    /// <summary>git add for a file, a folder, a repository or all of them.</summary>
    [RelayCommand(CanExecute = nameof(CanStage))]
    Task StageAsync(GitTreeNode? node)
    {
        if (node is null)
            return Task.CompletedTask;
        var targets = Targets(node).ToList();
        return RunAsync(null, targets.Select(t => t.Repo).ToList(), repo =>
            targets.First(t => t.Repo == repo).Path is { } path
                ? GitProcess.RunGitAsync(repo.Root, $"add -A -- {Quote(path)}")
                : GitProcess.RunGitAsync(repo.Root, "add -A"));
    }

    /// <summary>git reset for a file (and the name it had before a rename), a folder, a repository or all of them.</summary>
    [RelayCommand(CanExecute = nameof(CanStage))]
    Task UnstageAsync(GitTreeNode? node)
    {
        if (node is null)
            return Task.CompletedTask;
        var targets = Targets(node).ToList();
        return RunAsync(null, targets.Select(t => t.Repo).ToList(), repo =>
            targets.First(t => t.Repo == repo).Path is { } path
                ? GitProcess.RunGitAsync(repo.Root, $"reset -q -- {(node.Change is { } change ? Paths(change) : Quote(path))}")
                : GitProcess.RunGitAsync(repo.Root, "reset -q"));
    }

    bool CanStageAll => !IsBusy && UnstagedChanges.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    Task StageAllAsync() =>
        RunAsync(null, UnstagedChanges.Select(c => c.Repo).Distinct().ToList(), repo => GitProcess.RunGitAsync(repo.Root, "add -A"));

    bool CanUnstageAll => !IsBusy && StagedChanges.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    Task UnstageAllAsync() =>
        RunAsync(null, StagedChanges.Select(c => c.Repo).Distinct().ToList(), repo => GitProcess.RunGitAsync(repo.Root, "reset -q"));

    static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    static string Paths(GitChange change) => change.OriginalPath is { } original ? $"{Quote(change.Path)} {Quote(original)}" : Quote(change.Path);

    bool CanGenerate => !IsBusy && IsRepository;

    /// <summary>
    /// Claude (a fast model, without tools) reads the diff of every repository in scope and the latest commit subjects
    /// and writes one message in their style. Only the text comes back; nothing is committed until the user presses Commit.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    async Task GenerateAsync()
    {
        var claude = _claude();
        if (claude.CliPath is not { } cli)
        {
            Error = "Claude Code is not installed";
            return;
        }
        IsBusy = true;
        Error = null;
        Status = "Claude is writing the commit message…";
        try
        {
            var stagedOnly = CommitsStaged;
            var rows = (stagedOnly ? StagedChanges : UnstagedChanges).ToList();
            var repositories = rows.Select(c => c.Repo).Distinct().ToList();
            var several = repositories.Count > 1;
            var diff = new StringBuilder();
            var log = new StringBuilder();
            foreach (var repo in repositories)
            {
                var text = (await GitProcess.RunGitAsync(repo.Root, stagedOnly ? "diff --cached" : "diff HEAD")).Output;
                if (!stagedOnly)
                {
                    // New files have no diff against HEAD: show their content too (small text files only).
                    foreach (var change in repo.Changes.Where(c => c.Code == "??").Take(20))
                    {
                        var path = Path.Combine(repo.Root, change.Path);
                        if (File.Exists(path) && new FileInfo(path).Length < 20_000)
                            text += $"\n--- new file: {change.Path}\n{File.ReadAllText(path)}";
                    }
                }
                if (text.Trim().Length == 0)
                    continue;
                if (several)
                    diff.AppendLine($"=== Repository: {repo.Name}");
                diff.AppendLine(text);
                log.AppendLine((await GitProcess.RunGitAsync(repo.Root, "log -n 12 --format=%s")).Output.Trim());
            }
            if (diff.ToString().Trim().Length == 0)
            {
                Error = "There are no changes to describe.";
                return;
            }
            var diffText = diff.Length > MaxDiffChars ? diff.ToString(0, MaxDiffChars) + "\n[… the diff goes on; it was cut here]" : diff.ToString();
            var recent = string.Join("\n", log.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().Take(15));
            var files = string.Join("\n", rows.Select(c => several ? $"{c.Kind}: {c.Repo.Name}/{c.Path}" : $"{c.Kind}: {c.Path}"));

            var prompt = new StringBuilder()
                .AppendLine(several
                    ? "Write one git commit message for the changes below. They span several repositories and the same message is used in each."
                    : "Write a git commit message for the changes below.")
                .AppendLine("Rules:")
                .AppendLine("- First line: a short summary in the imperative mood, at most 72 characters, no period at the end.")
                .AppendLine("- If the change is more than trivial: a blank line, then a body wrapped at 72 characters that says what changed and why.")
                .AppendLine("- Follow the language, tone and conventions (prefixes, capitalization) of the recent commit messages.")
                .AppendLine("- Output only the commit message: no code fences, no quotes, no explanations, no trailers such as Co-Authored-By or Signed-off-by, and no mention of Claude or AI.")
                .AppendLine()
                .AppendLine($"Branch: {BranchLabel}")
                .AppendLine()
                .AppendLine("Recent commit messages:")
                .AppendLine(recent.Length > 0 ? recent : "(none yet)")
                .AppendLine()
                .AppendLine("Changed files:")
                .AppendLine(files)
                .AppendLine()
                .AppendLine("Diff:")
                .AppendLine(diffText)
                .ToString();
            var account = claude.SelectedAccount;
            var result = await GitProcess.RunAsync(cli, "-p --model haiku --tools \"\" --no-session-persistence --output-format text", repositories[0].Root, prompt, environment =>
            {
                environment.Remove("CLAUDECODE");
                account?.Apply(environment!);
            });
            if (!result.Ok || CleanMessage(result.Output) is not { Length: > 0 } message)
            {
                Error = "Claude could not write the message: " + (result.Problem is { Length: > 0 } problem ? problem : "no answer");
                return;
            }
            Message = message;
            Status = "Check the message, then commit.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The message without fences, quotes or trailers a model may add despite the instructions.</summary>
    internal static string CleanMessage(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim().Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(lines.Count - 1);
        lines.RemoveAll(l => Trailer.IsMatch(l));
        return string.Join(Environment.NewLine, lines).Trim().Trim('"');
    }

    static readonly Regex Trailer = new(@"^\s*(Co-Authored-By|Signed-off-by|Generated with|🤖)", RegexOptions.IgnoreCase);

    bool CanCommit => !IsBusy && Message.Trim().Length > 0 && (StagedChanges.Count > 0 || UnstagedChanges.Count > 0);

    /// <summary>
    /// git commit in every repository in scope that has something to commit, with the user's own identity (git config);
    /// the message goes in exactly as written. With staged changes only those are committed, otherwise all (git add -A).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    async Task CommitAsync()
    {
        var stagedOnly = CommitsStaged;
        var repositories = Scope.Where(r => stagedOnly ? r.HasStaged : r.Changes.Count > 0).ToList();
        var message = Message.Replace("\r\n", "\n").Trim() + "\n";
        var hashes = new List<string>();
        var committed = await RunAsync("Committing…", repositories, async repo =>
        {
            if (!stagedOnly)
            {
                var add = await GitProcess.RunGitAsync(repo.Root, "add -A");
                if (!add.Ok)
                    return add;
            }
            var commit = await GitProcess.RunGitAsync(repo.Root, "commit --cleanup=whitespace -F -", message);
            if (commit.Ok)
                hashes.Add((await GitProcess.RunGitAsync(repo.Root, "rev-parse --short HEAD")).Output.Trim());
            return commit;
        });
        if (committed > 0)
        {
            Message = "";
            Status = committed == 1 ? $"Committed {hashes[0]}" : $"Committed to {committed} repositories";
        }
    }

    bool CanSync => !IsBusy && IsRepository;

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task FetchAsync()
    {
        if (await RunAsync("Fetching…", Scope, repo => GitProcess.RunGitAsync(repo.Root, "fetch --prune")) == Scope.Count)
            Status = "Fetched";
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task PullAsync()
    {
        if (await RunAsync("Pulling…", Scope, PullAsync) == Scope.Count)
            Status = "Pulled";
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task PushAsync()
    {
        if (await RunAsync("Pushing…", Scope, PushAsync) == Scope.Count)
            Status = "Pushed";
    }

    /// <summary>Pull, then push: Git Changes' Sync.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task SyncAsync()
    {
        var done = await RunAsync("Syncing…", Scope, async repo =>
        {
            var pull = await PullAsync(repo);
            return pull.Ok ? await PushAsync(repo) : pull;
        });
        if (done == Scope.Count)
            Status = "Synced";
    }

    /// <summary>git pull; a branch with no upstream has nothing to pull and is left alone.</summary>
    static async Task<ProcessResult> PullAsync(GitRepo repo)
    {
        var upstream = await GitProcess.RunGitAsync(repo.Root, "rev-parse --abbrev-ref @{u}");
        return upstream.Ok ? await GitProcess.RunGitAsync(repo.Root, "pull --no-edit") : upstream with { ExitCode = 0 };
    }

    /// <summary>git push; a branch with no upstream yet goes to origin under its own name.</summary>
    static async Task<ProcessResult> PushAsync(GitRepo repo)
    {
        var upstream = await GitProcess.RunGitAsync(repo.Root, "rev-parse --abbrev-ref @{u}");
        return upstream.Ok ? await GitProcess.RunGitAsync(repo.Root, "push") : await GitProcess.RunGitAsync(repo.Root, "push -u origin HEAD");
    }

    /// <summary>
    /// Runs the work in each repository (one after another: credential prompts and hooks stay readable), then refreshes.
    /// Failures are listed by repository; returns how many succeeded.
    /// </summary>
    async Task<int> RunAsync(string? doing, IReadOnlyList<GitRepo> repositories, Func<GitRepo, Task<ProcessResult>> work)
    {
        IsBusy = true;
        Error = null;
        Status = doing;
        var succeeded = 0;
        var problems = new List<string>();
        try
        {
            foreach (var repo in repositories)
            {
                var result = await work(repo);
                if (result.Ok)
                    succeeded++;
                else
                    problems.Add(repositories.Count > 1 || IsMultiRepository ? $"{repo.Name}: {result.Problem}" : result.Problem);
            }
            Status = null;
            await RefreshAsync();
            if (problems.Count > 0)
                Error = string.Join("\n", problems);
        }
        finally
        {
            IsBusy = false;
        }
        return succeeded;
    }
}
