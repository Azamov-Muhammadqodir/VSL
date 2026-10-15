using System.Diagnostics;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSL.Services;
using VSL.ViewModels;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

namespace VSL.VisualStudio;

/// <summary>
/// What the Claude Code panel needs from Visual Studio: the solution folder to run in, the active editor and
/// selection, the files for @-mentions, opening files at a line, the diff window and the browser.
/// </summary>
public sealed class VisualStudioHost : IAsyncDisposable
{
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages" };

    readonly AsyncPackage _package;
    readonly Settings _settings = Settings.Load();
    ClaudeViewModel? _claude;
    List<string>? _files;

    public VisualStudioHost(AsyncPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _package = package;
        SolutionEvents.OnAfterOpenSolution += OnSolutionChanged;
        SolutionEvents.OnAfterCloseSolution += OnSolutionChanged;
        SolutionEvents.OnAfterOpenFolder += OnSolutionChanged;
        SolutionEvents.OnAfterCloseFolder += OnSolutionChanged;
        Views.Loc.Instance.Language = _settings.UiLanguage
            ?? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uz" ? "uz" : "en");
        Views.Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Views.Loc.Language))
                return;
            _settings.UiLanguage = Views.Loc.Instance.Language;
            _settings.Save();
        };
    }

    public ClaudeViewModel Claude
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _claude ??= CreateClaude();
        }
    }

    ClaudeViewModel CreateClaude()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var claude = new ClaudeViewModel(_settings, WorkingDirectory, EditorContext, ProjectFiles);
        claude.OpenFileRequested += (path, line) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenFile(path, line);
        };
        claude.DiffRequested += ShowDiff;
        // Visual Studio notices changed files itself (and reloads or asks, as set in Options); only the
        // @-mention list needs refreshing.
        claude.FilesChanged += () => _files = null;
        claude.AttentionRequested += () => _ = ShowWindowAsync();
        claude.OpenUrlRequested += OpenUrl;
        claude.SignInRequested += account => SignIn(claude, account);
        return claude;
    }

    /// <summary>
    /// The CLI signs in interactively only: open it for the account in a PowerShell window (like running
    /// <c>claude2</c> in a terminal); a new account's first start walks through the sign-in.
    /// </summary>
    void SignIn(ClaudeViewModel claude, Claude.ClaudeAccount account)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (claude.CliPath is not { } cli)
            return;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory(),
            Arguments = $"-NoLogo -NoExit -Command \"$Host.UI.RawUI.WindowTitle = 'Claude Code - {account.Name.Replace("'", "''")}'; " +
                $"Write-Host 'Signing in to the {account.Name.Replace("'", "''")} account. Use /login if Claude does not ask; close this window when you are done.'; " +
                $"& '{cli.Replace("'", "''")}'\"",
        };
        psi.Environment.Remove("CLAUDECODE");
        account.Apply(psi.Environment);
        try
        {
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            claude.Items.Add(new NoticeChatItem("Could not open a terminal to sign in: " + ex.Message, isError: true));
        }
    }

    DTE2? Dte
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Package.GetGlobalService(typeof(DTE)) as DTE2;
        }
    }

    /// <summary>The solution's folder (or the open folder), else the user's home.</summary>
    public string WorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution
            && solution.GetSolutionInfo(out var directory, out _, out _) == VSConstants.S_OK
            && directory is { Length: > 0 } && Directory.Exists(directory))
            return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>The active document and its selection (the last editor used, even while the panel has focus).</summary>
    ClaudeEditorContext? EditorContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Dte?.ActiveDocument is not { } document || document.FullName is not { Length: > 0 } path || !File.Exists(path))
                return null;
            if (document.Selection is not TextSelection selection || selection.IsEmpty)
                return new ClaudeEditorContext(path, null, null, null);
            var start = selection.TopPoint.Line;
            var end = selection.BottomPoint.Line;
            // A selection of whole lines ends at the start of the next one.
            if (end > start && selection.BottomPoint.LineCharOffset == 1)
                end--;
            var text = selection.Text;
            return new ClaudeEditorContext(path, start, end, text.Length > 20000 ? text.Substring(0, 20000) : text);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // A designer or a document without text.
            return null;
        }
    }

    /// <summary>What is selected in Solution Explorer: files, folders, and projects (as their folders).</summary>
    public IReadOnlyList<string> SelectedSolutionItems()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = new List<string>();
        try
        {
            if (Dte?.SelectedItems is not { } selected)
                return paths;
            foreach (SelectedItem item in selected)
            {
                var path = item.ProjectItem is { } projectItem && projectItem.FileCount > 0 ? projectItem.FileNames[1]
                    : item.Project?.FullName is { Length: > 0 } project ? Path.GetDirectoryName(project)
                    : null;
                if (path is { Length: > 0 })
                    paths.Add(path.TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // A node without a file (References, a solution folder).
        }
        return paths;
    }

    /// <summary>Files offered for @-mentions: the solution folder without build output and VCS folders.</summary>
    IEnumerable<string> ProjectFiles()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_files is not null)
            return _files;
        var root = WorkingDirectory();
        var files = new List<string>();
        if (root == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            return _files = files;
        var pending = new Stack<string>([root]);
        while (pending.Count > 0 && files.Count < 5000)
        {
            var dir = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir));
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                        pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return _files = files;
    }

    void OpenFile(string path, int? line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            VsShellUtilities.OpenDocument(_package, path);
            if (line is { } l && l > 0 && Dte?.ActiveDocument?.Selection is TextSelection selection)
                selection.GotoLine(l, Select: false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or IOException)
        {
            Claude.Items.Add(new NoticeChatItem($"Could not open {path}: {ex.Message}", isError: true));
        }
    }

    /// <summary>Claude's proposed change in Visual Studio's diff window (current file left, proposal right).</summary>
    void ShowDiff(string path, string before, string after)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService diff)
            return;
        var folder = Path.Combine(Path.GetTempPath(), "VSL.ClaudeCode", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        var left = Path.Combine(folder, "current." + name);
        var right = Path.Combine(folder, "proposed." + name);
        File.WriteAllText(left, before);
        File.WriteAllText(right, after);
        diff.OpenComparisonWindow2(left, right, $"{name}: proposed by Claude", path, $"{name} (current)", $"{name} (proposed by Claude)", null, null,
            (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
    }

    static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            System.Diagnostics.Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    async Task ShowWindowAsync()
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        await _package.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, create: true, _package.DisposalToken);
    }

    /// <summary>Conversations belong to a folder: a new solution starts a fresh one there.</summary>
    void OnSolutionChanged(object? sender, EventArgs e)
    {
        _files = null;
        if (_claude is { } claude)
            _ = _package.JoinableTaskFactory.RunAsync(claude.OnWorkingDirectoryChangedAsync);
    }

    public async ValueTask DisposeAsync()
    {
        SolutionEvents.OnAfterOpenSolution -= OnSolutionChanged;
        SolutionEvents.OnAfterCloseSolution -= OnSolutionChanged;
        SolutionEvents.OnAfterOpenFolder -= OnSolutionChanged;
        SolutionEvents.OnAfterCloseFolder -= OnSolutionChanged;
        if (_claude is { } claude)
            await claude.DisposeAsync();
    }
}
