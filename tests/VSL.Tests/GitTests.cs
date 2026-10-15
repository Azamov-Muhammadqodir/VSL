using System.Collections.Concurrent;
using System.Diagnostics;
using VSL.Git;

namespace VSL.Tests;

public class LineDiffTests
{
    static IReadOnlyList<LineChange> Diff(string baseline, string current) =>
        LineDiff.Compute(LineDiff.SplitLines(baseline), LineDiff.SplitLines(current));

    [Fact]
    public void Identical_text_has_no_changes() => Assert.Empty(Diff("a\nb\nc", "a\nb\nc"));

    [Fact]
    public void Detects_added_modified_and_deleted_lines()
    {
        Assert.Equal([new LineChange(2, 2, LineChangeKind.Added)], Diff("a\nd", "a\nb\nc\nd"));
        Assert.Equal([new LineChange(2, 1, LineChangeKind.Modified)], Diff("a\nb\nc", "a\nB\nc"));
        Assert.Equal([new LineChange(1, 0, LineChangeKind.Deleted)], Diff("a\nb\nc", "a\nc"));
        Assert.Equal([new LineChange(1, 1, LineChangeKind.Modified), new LineChange(4, 1, LineChangeKind.Added)],
            Diff("x\nb\nc", "y\nb\nc\nd"));
    }
}

public class GitStatusParserTests
{
    [Fact]
    public async Task Parses_porcelain_v2_with_renames_and_untracked()
    {
        var repo = await TempRepo.CreateAsync();
        var porcelain = string.Join('\0',
            "# branch.oid 1234",
            "# branch.head feature/x",
            "# branch.upstream origin/feature/x",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 abc abc src/A.cs",
            "1 A. N... 000000 100644 100644 000 abc src/New.cs",
            "2 R. N... 100644 100644 100644 abc abc R100 src/Renamed.cs", "src/Old.cs",
            "1 MM N... 100644 100644 100644 abc abc both.cs",
            "? notes.txt",
            "");
        var status = repo.Parse(porcelain);
        Assert.Equal(new GitBranchStatus("feature/x", "origin/feature/x", 2, 1), status.Branch);
        Assert.Contains(status.Changes, c => c.RelativePath == "src/A.cs" && c.Kind == GitChangeKind.Modified && !c.Staged);
        Assert.Contains(status.Changes, c => c.RelativePath == "src/New.cs" && c.Kind == GitChangeKind.Added && c.Staged);
        var renamed = Assert.Single(status.Changes, c => c.Kind == GitChangeKind.Renamed);
        Assert.EndsWith("src/Old.cs", renamed.OriginalPath);
        Assert.Equal(2, status.Changes.Count(c => c.RelativePath == "both.cs"));
        Assert.Contains(status.Changes, c => c.RelativePath == "notes.txt" && c.Kind == GitChangeKind.Untracked);
    }
}

public class GitRepositoryTests
{
    [Fact]
    public async Task Status_stage_commit_branch_and_head_content()
    {
        var repo = await TempRepo.CreateAsync();
        var file = Path.Combine(repo.Root, "Program.cs");
        await File.WriteAllTextAsync(file, "Console.WriteLine(1);\n", TestContext.Current.CancellationToken);
        Assert.True((await repo.CommitAsync("initial", all: true)).Success);
        Assert.Equal("Console.WriteLine(1);\n", await repo.ShowHeadAsync(file));

        await File.WriteAllTextAsync(file, "Console.WriteLine(2);\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "New.cs"), "class New {}\n", TestContext.Current.CancellationToken);
        var status = await repo.StatusAsync();
        Assert.Equal("main", status.Branch.Branch);
        Assert.Contains(status.Changes, c => c.RelativePath == "Program.cs" && c.Kind == GitChangeKind.Modified && !c.Staged);
        Assert.Contains(status.Changes, c => c.RelativePath == "New.cs" && c.Kind == GitChangeKind.Untracked);
        Assert.Contains("+Console.WriteLine(2);", await repo.DiffAsync(status.Changes.First(c => c.RelativePath == "Program.cs")));

        Assert.True((await repo.StageAsync([file])).Success);
        status = await repo.StatusAsync();
        Assert.Contains(status.Changes, c => c.RelativePath == "Program.cs" && c.Staged);
        Assert.True((await repo.UnstageAsync([file])).Success);
        Assert.DoesNotContain((await repo.StatusAsync()).Changes, c => c.Staged);

        Assert.True((await repo.DiscardAsync((await repo.StatusAsync()).Changes.First(c => c.RelativePath == "New.cs"))).Success);
        Assert.False(File.Exists(Path.Combine(repo.Root, "New.cs")));

        Assert.True((await repo.CreateBranchAsync("feature")).Success);
        Assert.True((await repo.CommitAsync("change", all: true)).Success);
        Assert.Equal(["feature", "main"], await repo.BranchesAsync());
        Assert.True((await repo.SwitchAsync("main")).Success);
        Assert.Equal("Console.WriteLine(1);\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.Empty((await repo.StatusAsync()).Changes);
    }

    [Fact]
    public void Watcher_treats_head_index_and_refs_as_repository_state()
    {
        foreach (var path in new[] { "HEAD", "index", "packed-refs", "MERGE_HEAD", "refs/heads/main", "refs/remotes/origin/main" })
            Assert.True(GitWatcher.IsRepositoryState(path), path);
        foreach (var path in new[] { "index.lock", "refs/heads/main.lock", "objects/ab/cdef", "logs/HEAD", "COMMIT_EDITMSG", "FETCH_HEAD" })
            Assert.False(GitWatcher.IsRepositoryState(path), path);
    }

    [Fact]
    public async Task Watcher_reports_edits_and_commits_made_outside()
    {
        var repo = await TempRepo.CreateAsync();
        Assert.Equal(Path.Combine(repo.Root, ".git"), repo.GitDir);
        using var watcher = new GitWatcher(repo.Root, repo.GitDir, TimeSpan.FromMilliseconds(100));
        var events = new ConcurrentQueue<(bool Repository, IReadOnlyCollection<string> Files)>();
        watcher.Changed += (repository, files) => events.Enqueue((repository, files));

        var file = Path.Combine(repo.Root, "a.txt");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        Assert.True(await Eventually(() => events.Any(e => e.Files.Contains(file))));
        Assert.DoesNotContain(events, e => e.Repository);

        events.Clear();
        TempRepo.Git(repo.Root, "add", "-A");
        TempRepo.Git(repo.Root, "commit", "-q", "-m", "outside");
        Assert.True(await Eventually(() => events.Any(e => e.Repository)));
        // git's own bookkeeping (objects, logs, lock files) is not reported as working-tree edits.
        Assert.DoesNotContain(events, e => e.Files.Any(f => f.Contains("/.git", StringComparison.Ordinal)));
    }

    static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
            await Task.Delay(100);
        return condition();
    }
}

/// <summary>A throwaway repository with a fixed identity so commits work on any machine.</summary>
static class TempRepo
{
    public static async Task<GitRepository> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "VSL Tests");
        Git(dir, "config", "user.email", "tests@example.invalid");
        Git(dir, "config", "commit.gpgsign", "false");
        return (await GitRepository.OpenAsync(dir))!;
    }

    public static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }
}
