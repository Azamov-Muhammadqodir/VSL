using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.ViewModels;

namespace VSL.VisualStudio.Git;

/// <summary>A changed file as `git status` lists it.</summary>
public sealed record GitChange(string Code, string Path, bool IsStaged)
{
    /// <summary>One letter for the row: M(odified), A(dded, also untracked), D(eleted), R(enamed), U (conflict).</summary>
    public string Letter => Code == "??" ? "A" : Code.Trim() is { Length: > 0 } c ? c.Substring(0, 1) : "M";

    public string Kind => Letter switch
    {
        "A" => "Added",
        "D" => "Deleted",
        "R" => "Renamed",
        "U" => "Conflict",
        _ => "Modified",
    };
}

/// <summary>
/// The commit panel: the changes of the solution's repository, a commit message Claude can write from the diff, and
/// commit, pull and push. Every git command runs as the user, with their name and email from git config and their
/// credentials; the message gets no Claude attribution or trailers.
/// </summary>
public sealed partial class GitViewModel : ObservableObject
{
    /// <summary>The diff Claude reads is cut here; the file list still names every change.</summary>
    const int MaxDiffChars = 60_000;

    readonly Func<string> _workingDirectory;
    readonly Func<ClaudeViewModel> _claude;
    string? _root;

    public GitViewModel(Func<string> workingDirectory, Func<ClaudeViewModel> claude)
    {
        _workingDirectory = workingDirectory;
        _claude = claude;
    }

    public ObservableCollection<GitChange> Changes { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepository))]
    public partial string? Branch { get; set; }

    public bool IsRepository => Branch is not null;

    /// <summary>"Name &lt;email&gt;" from git config: who the commit is made as.</summary>
    [ObservableProperty]
    public partial string? Author { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string Message { get; set; } = "";

    /// <summary>Stage every change (git add -A) before committing; off when some changes are staged already.</summary>
    [ObservableProperty]
    public partial bool StageAll { get; set; } = true;

    [ObservableProperty]
    public partial bool HasStaged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AheadText))]
    public partial int Ahead { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BehindText))]
    public partial int Behind { get; set; }

    public string AheadText => Ahead > 0 ? Ahead.ToString() : "";

    public string BehindText => Behind > 0 ? Behind.ToString() : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(GenerateCommand), nameof(PullCommand), nameof(PushCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What is happening ("Writing the message…") or what just happened ("Committed a1b2c3d").</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    async Task ToggleAsync()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
            await RefreshAsync();
    }

    [RelayCommand]
    void Close() => IsOpen = false;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Error = null;
        var directory = _workingDirectory();
        var top = await GitProcess.RunGitAsync(directory, "rev-parse --show-toplevel");
        if (!top.Ok)
        {
            _root = null;
            Branch = null;
            Changes.Clear();
            Status = null;
            return;
        }
        _root = top.Output.Trim().Replace('/', Path.DirectorySeparatorChar);
        var branch = await GitProcess.RunGitAsync(_root, "branch --show-current");
        Branch = branch.Output.Trim() is { Length: > 0 } current ? current : "HEAD";
        var name = (await GitProcess.RunGitAsync(_root, "config user.name")).Output.Trim();
        var email = (await GitProcess.RunGitAsync(_root, "config user.email")).Output.Trim();
        Author = name.Length > 0 ? (email.Length > 0 ? $"{name} <{email}>" : name) : null;

        var status = await GitProcess.RunGitAsync(_root, "status --porcelain=v1 --untracked-files=all");
        Changes.Clear();
        foreach (var line in status.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3))
        {
            var code = line.Substring(0, 2);
            var path = line.Substring(3);
            if (path.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and >= 0)
                path = path.Substring(arrow + 4);
            Changes.Add(new GitChange(code, path.Trim('"'), code[0] is not ' ' and not '?'));
        }
        HasStaged = Changes.Any(c => c.IsStaged);
        StageAll = !HasStaged;

        var counts = await GitProcess.RunGitAsync(_root, "rev-list --left-right --count @{u}...HEAD");
        var parts = counts.Ok ? counts.Output.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries) : [];
        Behind = parts.Length == 2 && int.TryParse(parts[0], out var behind) ? behind : 0;
        Ahead = parts.Length == 2 && int.TryParse(parts[1], out var ahead) ? ahead : 0;
        CommitCommand.NotifyCanExecuteChanged();
    }

    bool CanGenerate => !IsBusy;

    /// <summary>
    /// Claude (a fast model, without tools) reads the diff and the latest commit subjects and writes a message in the
    /// repository's style. Only the text comes back; nothing is committed until the user presses Commit.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    async Task GenerateAsync()
    {
        var claude = _claude();
        if (_root is not { } root)
            return;
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
            var stagedOnly = HasStaged && !StageAll;
            var diff = (await GitProcess.RunGitAsync(root, stagedOnly ? "diff --cached" : "diff HEAD")).Output;
            if (!stagedOnly)
            {
                // New files have no diff against HEAD: show their content too (small text files only).
                foreach (var change in Changes.Where(c => c.Code == "??").Take(20))
                {
                    var path = Path.Combine(root, change.Path);
                    if (File.Exists(path) && new FileInfo(path).Length < 20_000)
                        diff += $"\n--- new file: {change.Path}\n{File.ReadAllText(path)}";
                }
            }
            if (diff.Trim().Length == 0)
            {
                Error = "There are no changes to describe.";
                return;
            }
            if (diff.Length > MaxDiffChars)
                diff = diff.Substring(0, MaxDiffChars) + "\n[… the diff goes on; it was cut here]";
            var log = (await GitProcess.RunGitAsync(root, "log -n 12 --format=%s")).Output.Trim();
            var files = string.Join("\n", Changes.Where(c => !stagedOnly || c.IsStaged).Select(c => $"{c.Kind}: {c.Path}"));

            var prompt = new StringBuilder()
                .AppendLine("Write a git commit message for the changes below.")
                .AppendLine("Rules:")
                .AppendLine("- First line: a short summary in the imperative mood, at most 72 characters, no period at the end.")
                .AppendLine("- If the change is more than trivial: a blank line, then a body wrapped at 72 characters that says what changed and why.")
                .AppendLine("- Follow the language, tone and conventions (prefixes, capitalization) of the recent commit messages.")
                .AppendLine("- Output only the commit message: no code fences, no quotes, no explanations, no trailers such as Co-Authored-By or Signed-off-by, and no mention of Claude or AI.")
                .AppendLine()
                .AppendLine($"Branch: {Branch}")
                .AppendLine()
                .AppendLine("Recent commit messages:")
                .AppendLine(log.Length > 0 ? log : "(none yet)")
                .AppendLine()
                .AppendLine("Changed files:")
                .AppendLine(files)
                .AppendLine()
                .AppendLine("Diff:")
                .AppendLine(diff)
                .ToString();
            var account = claude.SelectedAccount;
            var result = await GitProcess.RunAsync(cli, "-p --model haiku --tools \"\" --no-session-persistence --output-format text", root, prompt, environment =>
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

    bool CanCommit => !IsBusy && Message.Trim().Length > 0 && Changes.Count > 0;

    /// <summary>git commit with the user's own identity (git config); the message goes in exactly as written.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    async Task CommitAsync()
    {
        if (_root is not { } root)
            return;
        await RunAsync("Committing…", async () =>
        {
            if (StageAll)
            {
                var add = await GitProcess.RunGitAsync(root, "add -A");
                if (!add.Ok)
                    return add;
            }
            var message = Message.Replace("\r\n", "\n").Trim() + "\n";
            var commit = await GitProcess.RunGitAsync(root, "commit --cleanup=whitespace -F -", message);
            if (commit.Ok)
            {
                var hash = (await GitProcess.RunGitAsync(root, "rev-parse --short HEAD")).Output.Trim();
                Message = "";
                Status = $"Committed {hash}";
            }
            return commit;
        });
    }

    bool CanSync => !IsBusy && IsRepository;

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task PullAsync()
    {
        if (_root is not { } root)
            return;
        await RunAsync("Pulling…", async () =>
        {
            var pull = await GitProcess.RunGitAsync(root, "pull --no-edit");
            if (pull.Ok)
                Status = pull.Output.Contains("Already up to date") ? "Already up to date" : "Pulled";
            return pull;
        });
    }

    /// <summary>git push; a branch with no upstream yet goes to origin under its own name.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task PushAsync()
    {
        if (_root is not { } root)
            return;
        await RunAsync("Pushing…", async () =>
        {
            var upstream = await GitProcess.RunGitAsync(root, "rev-parse --abbrev-ref @{u}");
            var push = await GitProcess.RunGitAsync(root, upstream.Ok ? "push" : "push -u origin HEAD");
            if (push.Ok)
                Status = "Pushed";
            return push;
        });
    }

    async Task RunAsync(string doing, Func<Task<ProcessResult>> work)
    {
        IsBusy = true;
        Error = null;
        Status = doing;
        try
        {
            var result = await work();
            if (!result.Ok)
            {
                Error = result.Problem;
                Status = null;
            }
            var status = Status;
            await RefreshAsync();
            Status = status;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
