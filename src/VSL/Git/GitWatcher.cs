namespace VSL.Git;

/// <summary>
/// Notices changes made outside VSL (a commit or checkout in the terminal, another tool, files written on disk) so
/// Solution Explorer, Git Changes, the change margin and open editors follow them. Watches the solution folder and,
/// in a repository, the git directory; bursts (builds, checkouts) are coalesced.
/// </summary>
public sealed class GitWatcher : IDisposable
{
    static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    readonly string? _gitDir;
    readonly TimeSpan _delay;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly Timer _timer;
    readonly Lock _gate = new();
    readonly HashSet<string> _files = new(StringComparer.Ordinal);
    bool _repositoryChanged;
    DateTime? _firstPending;

    /// <summary>
    /// Raised on a thread-pool thread once changes settle: whether HEAD, the index or refs changed, and the
    /// working-tree files that changed.
    /// </summary>
    public event Action<bool, IReadOnlyCollection<string>>? Changed;

    /// <param name="gitDir">The repository's git directory, or null when the folder is not in a repository.</param>
    public GitWatcher(string workingFolder, string? gitDir, TimeSpan delay)
    {
        _gitDir = gitDir is null ? null : Path.GetFullPath(gitDir).TrimEnd('/');
        _delay = delay;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        var folder = Path.GetFullPath(workingFolder).TrimEnd('/');
        Watch(folder);
        if (_gitDir is not null && !IsUnder(_gitDir, folder))
            Watch(_gitDir);
    }

    void Watch(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        var fsw = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        fsw.Created += (_, e) => OnEvent(e.FullPath);
        fsw.Changed += (_, e) => OnEvent(e.FullPath);
        fsw.Deleted += (_, e) => OnEvent(e.FullPath);
        fsw.Renamed += (_, e) =>
        {
            OnEvent(e.OldFullPath);
            OnEvent(e.FullPath);
        };
        // Overflowed buffer: some events were lost, so re-read everything.
        fsw.Error += (_, _) => Pending(repository: true, null);
        fsw.EnableRaisingEvents = true;
        _watchers.Add(fsw);
    }

    void OnEvent(string path)
    {
        if (_gitDir is not null && IsUnder(path, _gitDir))
        {
            if (IsRepositoryState(path[(_gitDir.Length + 1)..]))
                Pending(repository: true, null);
        }
        else if (!path.Contains("/.git/", StringComparison.Ordinal) && !path.EndsWith("/.git", StringComparison.Ordinal))
        {
            Pending(repository: false, path);
        }
    }

    /// <summary>
    /// Files in the git directory whose change means status or HEAD moved (git writes them as *.lock and renames).
    /// Objects, logs and lock files are skipped: they change on every operation without saying anything new.
    /// </summary>
    internal static bool IsRepositoryState(string relative) =>
        !relative.EndsWith(".lock", StringComparison.Ordinal)
        && (relative is "HEAD" or "index" or "packed-refs" or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD"
            || relative.StartsWith("refs/", StringComparison.Ordinal));

    void Pending(bool repository, string? file)
    {
        lock (_gate)
        {
            _repositoryChanged |= repository;
            if (file is not null)
                _files.Add(file);
            var now = DateTime.UtcNow;
            _firstPending ??= now;
            // Trailing debounce, but a constant stream (a long build) still refreshes every couple of seconds.
            _timer.Change(now - _firstPending.Value >= MaxDelay ? TimeSpan.Zero : _delay, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        bool repository;
        string[] files;
        lock (_gate)
        {
            repository = _repositoryChanged;
            files = [.. _files];
            _repositoryChanged = false;
            _files.Clear();
            _firstPending = null;
        }
        if (repository || files.Length > 0)
            Changed?.Invoke(repository, files);
    }

    static bool IsUnder(string path, string directory) => path.StartsWith(directory + "/", StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var fsw in _watchers)
            fsw.Dispose();
        _timer.Dispose();
    }
}
