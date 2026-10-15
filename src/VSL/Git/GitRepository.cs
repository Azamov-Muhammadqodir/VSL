using System.Diagnostics;
using System.Text;

namespace VSL.Git;

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
}

public sealed record GitBranchStatus(string? Branch, string? Upstream, int Ahead, int Behind);

public sealed record GitFileChange(string Path, string RelativePath, GitChangeKind Kind, bool Staged, string? OriginalPath)
{
    /// <summary>VS / Git one-letter status.</summary>
    public string Letter => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        _ => "C",
    };
}

public sealed record GitStatus(GitBranchStatus Branch, IReadOnlyList<GitFileChange> Changes);

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Repository operations over the git CLI (the user's config, hooks and credential helpers apply).</summary>
public sealed class GitRepository
{
    GitRepository(string root, string gitDir)
    {
        Root = root;
        GitDir = gitDir;
    }

    public string Root { get; }

    /// <summary>The repository's git directory (usually Root/.git; elsewhere for worktrees).</summary>
    public string GitDir { get; }

    public static async Task<GitRepository?> OpenAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        var result = await RunAsync(directory, ["rev-parse", "--show-toplevel", "--absolute-git-dir"]);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return result.Success && lines.Length == 2 ? new GitRepository(lines[0], lines[1]) : null;
    }

    public string Relative(string path) => System.IO.Path.GetRelativePath(Root, path);

    public async Task<GitStatus> StatusAsync()
    {
        var result = await RunAsync(Root, ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"], optionalLocks: false);
        return Parse(result.Output);
    }

    internal GitStatus Parse(string porcelain)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitFileChange>();
        var tokens = porcelain.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0)
                continue;
            if (t.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = t["# branch.head ".Length..];
                if (branch == "(detached)")
                    branch = null;
            }
            else if (t.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = t["# branch.upstream ".Length..];
            }
            else if (t.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = t["# branch.ab ".Length..].Split(' ');
                ahead = int.Parse(parts[0].TrimStart('+'));
                behind = int.Parse(parts[1].TrimStart('-'));
            }
            else if (t.StartsWith("? ", StringComparison.Ordinal))
            {
                Add(t[2..], GitChangeKind.Untracked, staged: false, null);
            }
            else if (t[0] is '1' or '2' or 'u')
            {
                var fieldCount = t[0] switch { '1' => 8, '2' => 9, _ => 10 };
                var fields = t.Split(' ', fieldCount + 1);
                var xy = fields[1];
                var path = fields[fieldCount];
                string? original = null;
                if (t[0] == '2' && i + 1 < tokens.Length)
                    original = tokens[++i];
                if (t[0] == 'u')
                {
                    Add(path, GitChangeKind.Conflicted, staged: false, null);
                    continue;
                }
                if (xy[0] != '.')
                    Add(path, KindOf(xy[0]), staged: true, original);
                if (xy[1] != '.')
                    Add(path, KindOf(xy[1]), staged: false, original);
            }
        }
        return new GitStatus(new GitBranchStatus(branch, upstream, ahead, behind), changes);

        void Add(string relative, GitChangeKind kind, bool staged, string? original) =>
            changes.Add(new GitFileChange(System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative)), relative, kind, staged,
                original is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, original))));

        static GitChangeKind KindOf(char c) => c switch
        {
            'A' or 'C' => GitChangeKind.Added,
            'D' => GitChangeKind.Deleted,
            'R' => GitChangeKind.Renamed,
            'U' => GitChangeKind.Conflicted,
            _ => GitChangeKind.Modified,
        };
    }

    public Task<GitResult> StageAsync(IEnumerable<string> paths) => RunAsync(Root, ["add", "--", .. paths.Select(Relative)]);

    public Task<GitResult> UnstageAsync(IEnumerable<string> paths) => RunAsync(Root, ["restore", "--staged", "--", .. paths.Select(Relative)]);

    /// <summary>Undo working-tree changes; untracked files are deleted like VS's "Undo Changes".</summary>
    public async Task<GitResult> DiscardAsync(GitFileChange change)
    {
        if (change.Kind == GitChangeKind.Untracked)
        {
            File.Delete(change.Path);
            return new GitResult(0, "");
        }
        return await RunAsync(Root, ["restore", "--", Relative(change.Path)]);
    }

    /// <summary>Commits staged changes, or every change when <paramref name="all"/> (VS "Commit All").</summary>
    public async Task<GitResult> CommitAsync(string message, bool all)
    {
        if (all)
        {
            var add = await RunAsync(Root, ["add", "--all"]);
            if (!add.Success)
                return add;
        }
        return await RunAsync(Root, ["commit", "-m", message]);
    }

    public Task<GitResult> FetchAsync(Action<string>? output = null) => RunAsync(Root, ["fetch", "--prune"], output: output);

    public Task<GitResult> PullAsync(Action<string>? output = null) => RunAsync(Root, ["pull", "--ff-only"], output: output);

    public async Task<GitResult> PushAsync(GitBranchStatus branch, Action<string>? output = null) =>
        branch.Upstream is null && branch.Branch is { } name
            ? await RunAsync(Root, ["push", "--set-upstream", "origin", name], output: output)
            : await RunAsync(Root, ["push"], output: output);

    public async Task<IReadOnlyList<string>> BranchesAsync()
    {
        var result = await RunAsync(Root, ["for-each-ref", "--format=%(refname:short)", "refs/heads"]);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList();
    }

    public Task<GitResult> SwitchAsync(string branch) => RunAsync(Root, ["switch", branch]);

    public Task<GitResult> CreateBranchAsync(string name) => RunAsync(Root, ["switch", "-c", name]);

    /// <summary>The file's content at HEAD, or null when it is not tracked there.</summary>
    public async Task<string?> ShowHeadAsync(string path)
    {
        var rel = Relative(path).Replace('\\', '/');
        var result = await RunAsync(Root, ["show", $"HEAD:{rel}"], optionalLocks: false);
        return result.Success ? result.Output : null;
    }

    /// <summary>The file's staged content (the index), or null when it is not in the index.</summary>
    public async Task<string?> ShowIndexAsync(string path)
    {
        var rel = Relative(path).Replace('\\', '/');
        var result = await RunAsync(Root, ["show", $":{rel}"], optionalLocks: false);
        return result.Success ? result.Output : null;
    }

    /// <summary>Unified diff of the working file (or the staged version) against HEAD, for the diff view.</summary>
    public async Task<string> DiffAsync(GitFileChange change)
    {
        var rel = Relative(change.Path);
        var result = change.Kind == GitChangeKind.Untracked
            ? await RunAsync(Root, ["diff", "--no-color", "--no-index", "--", "/dev/null", rel], optionalLocks: false)
            : await RunAsync(Root, change.Staged ? ["diff", "--no-color", "--cached", "--", rel] : ["diff", "--no-color", "--", rel], optionalLocks: false);
        return result.Output;
    }

    static async Task<GitResult> RunAsync(string directory, IReadOnlyList<string> args, bool optionalLocks = true, Action<string>? output = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Never block on a credential prompt the user cannot see; keep output parseable.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        if (!optionalLocks)
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var stdout = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            var sb = new StringBuilder();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                sb.AppendLine(line);
                output?.Invoke(line);
            }
            return sb.ToString();
        });
        var buffer = new char[8192];
        int n;
        while ((n = await process.StandardOutput.ReadAsync(buffer)) > 0)
        {
            stdout.Append(buffer, 0, n);
            if (output is not null)
                foreach (var line in new string(buffer, 0, n).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    output(line);
        }
        var stderr = await stderrTask;
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, process.ExitCode == 0 ? stdout.ToString() : stdout + stderr);
    }
}
