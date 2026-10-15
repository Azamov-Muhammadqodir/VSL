using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VSL.Claude;
using VSL.ViewModels;
using VSL.Testing;
using VSL.Views;

[assembly: AvaloniaTestApplication(typeof(VSL.Tests.TestAppBuilder))]

namespace VSL.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        SampleSolution.EnsureConfigIsolated();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>Drives the real main window headlessly and saves screenshots for review.</summary>
[Collection(SampleSolutionCollection.Name)]
public class UiTests
{
    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A view model with no remembered per-solution state, so tests do not depend on each other's order.</summary>
    static MainViewModel NewViewModel()
    {
        // The Claude Code window must never start the user's real CLI from a test.
        FakeClaude.Ensure();
        var vm = new MainViewModel();
        vm.Settings.Solutions.Clear();
        vm.Settings.ClaudePermissionMode = null;
        vm.Settings.ClaudeModel = null;
        vm.Settings.EditorZoom = 1.0;
        VSL.Editor.EditorHost.DefaultZoom = 1.0;
        return vm;
    }

    static string Snapshot(Avalonia.Controls.Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(SampleSolution.ArtifactsDir, name + ".png");
        frame?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    [AvaloniaFact]
    public async Task Build_reports_errors_and_run_starts_web_app()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        // A clean build succeeds.
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        Assert.Contains("========== Build: succeeded", vm.BuildOutput.Document.Text);
        Assert.Equal(0, vm.ErrorCount);

        // A compile error on disk lands in the Error List with file, line and project.
        var program = SampleSolution.PathOf(sln, "Web/Program.cs");
        var original = await File.ReadAllTextAsync(program, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(program, original + "\nint broken = \"text\";\n", TestContext.Current.CancellationToken);
        try
        {
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            Assert.Contains("========== Build: failed", vm.BuildOutput.Document.Text);
            var error = Assert.Single(vm.Errors, e => e.Code == "CS0029");
            Assert.Equal("Web", error.Project);
            Assert.Equal(program, error.FilePath);
            Assert.True(window.IsToolWindowShowing(ToolWindows.ErrorList));
        }
        finally
        {
            await File.WriteAllTextAsync(program, original, CancellationToken.None);
        }

        // Ctrl+F5 builds, starts the web host with the launch profile and Stop ends it.
        var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("Now listening on: http://localhost:5280"), TimeSpan.FromMinutes(2));
        Assert.Contains("Now listening on: http://localhost:5280", vm.DebugOutput.Document.Text);
        Assert.True(vm.IsRunning);
        vm.StopCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(vm.IsRunning);
        Assert.Contains("has exited with code", vm.DebugOutput.Document.Text);

        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Debugs_with_breakpoints_locals_watch_and_stepping()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var program = SampleSolution.PathOf(sln, "Web/Program.cs");
        var doc = vm.OpenDocument(program)!;
        var host = doc.Host;

        // Breakpoints follow their line when text is inserted above them (editor anchors).
        host.ToggleBreakpoint(4);
        host.Document.Insert(0, "// moved\n");
        Assert.Equal([5], vm.Breakpoints.For(program));
        host.Editor.Undo();
        Assert.Equal([4], vm.Breakpoints.For(program));
        host.ToggleBreakpoint(4);
        Assert.Empty(vm.Breakpoints.For(program));
        Assert.False(doc.IsDirty);

        // F9 equivalent on "var app = builder.Build();", then F5.
        host.ToggleBreakpoint(2);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break, TimeSpan.FromMinutes(2));
        Assert.True(vm.Mode == DebugMode.Break, "did not break; output:\n" + vm.DebugOutput.Document.Text);
        Assert.Equal(2, vm.CallStack[0].Line);
        Assert.Equal(vm.CallStack[0], vm.SelectedFrame);
        await WaitUntil(() => host.ExecutionLine == 2 && vm.Locals.Count > 0, TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.ExecutionLine);
        Assert.Contains(vm.Locals, v => v.Name == "builder");
        Assert.True(vm.Breakpoints.IsVerified(program, 2));

        // Watch window and hover data tip evaluate in the selected frame.
        await vm.AddWatchCommand.ExecuteAsync("builder.Environment.EnvironmentName");
        Assert.Equal("\"Development\"", vm.Watches.Single().Value);
        Assert.Equal("\"Development\"", await host.DataTipProvider!("builder.Environment.EnvironmentName"));
        Snapshot(window, "debug-break");

        // F10 steps to the next statement.
        await vm.StepOverCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0 && vm.CallStack[0].Line == 4, TimeSpan.FromSeconds(30));
        Assert.Equal(4, vm.CallStack[0].Line);
        await WaitUntil(() => host.ExecutionLine == 4, TimeSpan.FromSeconds(5));
        Assert.Equal(4, host.ExecutionLine);

        // F5 continues; the host starts; Shift+F5 stops and clears debug state.
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("Now listening on: http://localhost:5280"), TimeSpan.FromSeconds(60));
        Assert.Equal(DebugMode.Running, vm.Mode);
        Assert.Null(host.ExecutionLine);
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        Assert.Equal(DebugMode.Design, vm.Mode);
        Assert.Empty(vm.CallStack);
        Assert.Contains("has exited with code", vm.DebugOutput.Document.Text);

        // Breakpoints and watches persist with the solution.
        host.ToggleBreakpoint(2);
        vm.RemoveWatchCommand.Execute(vm.Watches.Single());
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Quick_action_adds_missing_using()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var doc = vm.OpenDocument(SampleSolution.PathOf(sln, "Components/Greeting.razor.cs"))!;
        var host = doc.Host;
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));

        var text = host.Document.Text;
        host.Editor.CaretOffset = text.IndexOf("    [Parameter]", StringComparison.Ordinal);
        host.Editor.TextArea.PerformTextInput("    public StringBuilder Sb { get; } = new();\n");
        await WaitUntil(() => host.Diagnostics.Any(d => d.Code == "CS0246"), TimeSpan.FromSeconds(60));
        host.Editor.CaretOffset = host.Document.Text.IndexOf("StringBuilder", StringComparison.Ordinal) + 3;

        // Ctrl+. opens the lightbulb menu at the caret.
        var menu = await host.ShowQuickActionsAsync();
        Assert.NotNull(menu);
        Assert.True(menu.IsOpen);
        Assert.Contains(menu.Items.OfType<Avalonia.Controls.MenuItem>(), i => (string?)i.Header == "using System.Text;");
        menu.Close();

        // Applying a fix edits the buffer as a single undo step.
        var actions = await host.GetQuickActionsAsync();
        Assert.Contains(actions, a => a.Title.StartsWith("Generate class", StringComparison.Ordinal));
        await host.ApplyCodeActionAsync(actions.Single(a => a.Title == "using System.Text;"));
        Assert.StartsWith("using System.Text;", host.Document.Text);
        await WaitUntil(() => !host.Diagnostics.Any(d => d.Code == "CS0246"), TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(host.Diagnostics, d => d.Code == "CS0246");

        host.Editor.Undo();
        Assert.False(host.Document.Text.StartsWith("using System.Text;", StringComparison.Ordinal));
        while (host.Editor.CanUndo)
            host.Editor.Undo();
        Assert.False(doc.IsDirty);
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Test_explorer_runs_tests_and_shows_results()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.True(window.IsToolWindowShowing(ToolWindows.TestExplorer));
        Assert.False(vm.IsTesting);
        Assert.Equal((3, 1, 1), (vm.PassedCount, vm.FailedCount, vm.SkippedCount));
        Assert.StartsWith("Test run finished: 5 test(s) (3 Passed, 1 Failed, 1 Skipped)", vm.TestSummary);

        // Project > Namespace > Class > tests, with theory rows grouped under their method.
        var project = Assert.Single(vm.TestRoots);
        Assert.Equal("Tests", project.Name);
        Assert.Equal(TestState.Failed, project.State);
        var cls = project.Children.Single().Children.Single();
        Assert.Equal("GreetingTests", cls.Name);
        var adds = cls.Children.Single(c => c.Name == "Adds");
        Assert.Equal(TestNodeKind.Method, adds.Kind);
        Assert.Equal(["Adds(a: 1, b: 2, sum: 3)", "Adds(a: 2, b: 2, sum: 4)"], adds.Children.Select(c => c.Name));
        Assert.Equal(TestState.Passed, adds.State);
        Assert.Equal(TestState.Skipped, cls.Children.Single(c => c.Name == "Skipped").State);
        var failing = cls.Children.Single(c => c.Name == "Deliberately_failing");
        Assert.Contains("Assert.Equal() Failure", failing.ErrorMessage);
        vm.SelectedTest = failing;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "test-explorer");

        // Run Failed reruns just that test; double-click navigates to it.
        await vm.RunFailedTestsCommand.ExecuteAsync(null);
        Assert.StartsWith("Test run finished: 1 test(s)", vm.TestSummary);
        vm.NavigateToTest(failing);
        await WaitUntil(() => vm.ActiveEditor?.FileName == "GreetingTests.cs", TimeSpan.FromSeconds(5));
        await WaitUntil(() => vm.ActiveEditor!.Host.CaretPosition.Line == 17, TimeSpan.FromSeconds(5));
        Assert.Equal(17, vm.ActiveEditor!.Host.CaretPosition.Line);

        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.Empty(vm.TestRoots);
    }

    [AvaloniaFact]
    public async Task Git_changes_margin_badges_commit_and_branches()
    {
        // A fresh repository holding the sample sources (no bin/obj), committed once.
        var dir = Path.Combine(Path.GetTempPath(), "vsl-gitui-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        // Like any real .NET repository: build and restore output is ignored.
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        TempRepo.Git(dir, "init", "-q", "-b", "main");
        TempRepo.Git(dir, "config", "user.name", "VSL Tests");
        TempRepo.Git(dir, "config", "user.email", "tests@example.invalid");
        TempRepo.Git(dir, "config", "commit.gpgsign", "false");
        TempRepo.Git(dir, "add", "-A");
        TempRepo.Git(dir, "commit", "-q", "-m", "initial");

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(Path.Combine(dir, "BlazorSample.sln"));
        await WaitUntil(() => vm.HasGit && vm.GitBranch == "main", TimeSpan.FromSeconds(10));
        Assert.True(vm.GitChangeCount == 0, string.Join(", ", vm.UnstagedChanges.Concat(vm.StagedChanges).Select(c => c.Letter + " " + c.Change.RelativePath)));

        // Typing shows a green "added" bar before anything is saved.
        var program = Path.Combine(dir, "Web", "Program.cs");
        var doc = vm.OpenDocument(program)!;
        var host = doc.Host;
        await WaitUntil(() => host.Editor.TextArea.LeftMargins.OfType<VSL.Editor.GitChangeMargin>().Any(), TimeSpan.FromSeconds(10));
        host.Document.Insert(host.Document.TextLength, "// changed\n");
        await WaitUntil(() => host.GitChanges.Any(c => c.Kind == VSL.Git.LineChangeKind.Added), TimeSpan.FromSeconds(5));
        Assert.Contains(host.GitChanges, c => c.Kind == VSL.Git.LineChangeKind.Added);
        host.Document.Insert(host.Document.GetLineByNumber(2).EndOffset, " // note");
        await WaitUntil(() => host.GitChanges.Any(c => c.Kind == VSL.Git.LineChangeKind.Modified), TimeSpan.FromSeconds(5));
        Assert.Contains(host.GitChanges, c => c is { Kind: VSL.Git.LineChangeKind.Modified, Line: 2 });
        Snapshot(window, "git-margin");

        // Saving lists the file under Changes and badges it in Solution Explorer.
        vm.SaveCommand.Execute(null);
        await WaitUntil(() => vm.UnstagedChanges.Any(c => c.FileName == "Program.cs"), TimeSpan.FromSeconds(10));
        var web = vm.ExplorerRoots[0].Children.First(n => n.Name == "Web");
        web.IsExpanded = true;
        Assert.Equal("M", web.Children.First(n => n.Name == "Program.cs").GitBadge);
        Assert.Equal("Commit All", vm.CommitButtonText);

        // Stage, then Commit Staged clears the list and the margin.
        await vm.StageCommand.ExecuteAsync(vm.UnstagedChanges.First(c => c.FileName == "Program.cs"));
        Assert.Single(vm.StagedChanges);
        Assert.Equal("Commit Staged", vm.CommitButtonText);
        vm.CommitMessage = "Edit Program.cs";
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.GitChangeCount);
        Assert.Equal("", vm.CommitMessage);
        await WaitUntil(() => host.GitChanges.Count == 0, TimeSpan.FromSeconds(5));
        Assert.Empty(host.GitChanges);

        // New branch, a change on it, and the read-only diff view.
        await vm.CreateBranchAsync("feature/x");
        Assert.Equal("feature/x", vm.GitBranch);
        Assert.Equal(["feature/x", "main"], vm.Branches);
        host.Document.Insert(0, "// top\n");
        vm.ActiveDocument = doc;
        vm.SaveCommand.Execute(null);
        await WaitUntil(() => vm.UnstagedChanges.Count == 1, TimeSpan.FromSeconds(10));
        host.Document.Replace(host.Document.GetLineByNumber(3).Offset, 3, "VAR");
        vm.SaveCommand.Execute(null);
        await vm.OpenDiffCommand.ExecuteAsync(vm.UnstagedChanges[0]);
        var diff = Assert.IsType<DiffViewModel>(vm.ActiveDocument);
        Assert.Equal("Program.cs (diff)", diff.Title);
        // Line 1 was inserted (filler on the left) and line 3 changed; unchanged lines stay side by side.
        Assert.Equal(new VSL.Editor.DiffRow(-1, 0, VSL.Editor.DiffRowKind.Added), diff.View.Rows[0]);
        Assert.Equal(new VSL.Editor.DiffRow(0, 1, VSL.Editor.DiffRowKind.Equal), diff.View.Rows[1]);
        Assert.Equal(new VSL.Editor.DiffRow(1, 2, VSL.Editor.DiffRowKind.Modified), diff.View.Rows[2]);
        Assert.Equal(2, diff.View.ChangeCount);
        Assert.Equal(diff.View.LeftText.Split('\n').Length, diff.View.RightText.Split('\n').Length);
        diff.View.Go(1);
        diff.View.Go(1);
        Assert.Equal(1, diff.View.CurrentChange);
        vm.ShowToolWindow(ToolWindows.GitChanges);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "git-changes");
        vm.ActiveDocument = doc;
        await vm.CompareActiveWithUnmodifiedCommand.ExecuteAsync(null);
        Assert.IsType<DiffViewModel>(vm.ActiveDocument);

        await vm.SwitchBranchCommand.ExecuteAsync("main");
        Assert.Equal("main", vm.GitBranch);

        // In a narrow Git Changes window long names are trimmed and the row buttons stay visible.
        window.MoveToolWindow(ToolWindows.GitChanges, DockSide.Left);
        vm.ShowToolWindow(ToolWindows.GitChanges);
        const string longName = "AVeryLongFileNameThatCannotFitIntoTheNarrowGitChangesWindow.cs";
        File.WriteAllText(Path.Combine(dir, "Web", longName), "class Long {}\n");
        await WaitUntil(() => vm.UnstagedChanges.Any(c => c.FileName == longName), TimeSpan.FromSeconds(10));
        Assert.Contains(vm.UnstagedChanges, c => c.FileName == longName);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        var rowActions = window.GetVisualDescendants().OfType<Avalonia.Controls.StackPanel>().Where(p => p.Classes.Contains("rowActions") && p.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(rowActions);
        foreach (var actions in rowActions)
        {
            var list = actions.FindAncestorOfType<Avalonia.Controls.ListBox>()!;
            var right = actions.TranslatePoint(new Point(actions.Bounds.Width, 0), list)!.Value.X;
            Assert.True(right <= list.Bounds.Width + 0.5, $"row buttons end at {right}, list is {list.Bounds.Width} wide");
        }
        Snapshot(window, "git-changes-narrow");

        // A commit and a checkout made outside VSL (e.g. in the terminal) show up without any VSL action,
        // and the open editor follows the checkout.
        TempRepo.Git(dir, "add", "-A");
        TempRepo.Git(dir, "commit", "-q", "-m", "outside");
        await WaitUntil(() => vm.GitChangeCount == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, vm.GitChangeCount);
        Assert.StartsWith("// top", host.Document.Text);
        TempRepo.Git(dir, "switch", "-q", "feature/x");
        await WaitUntil(() => vm.GitBranch == "feature/x" && !host.Document.Text.StartsWith("// top", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Assert.Equal("feature/x", vm.GitBranch);
        Assert.False(host.Document.Text.StartsWith("// top", StringComparison.Ordinal));
        Assert.False(doc.IsDirty);
        window.ResetWindowLayout();

        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.False(vm.HasGit);
    }

    [AvaloniaFact]
    public async Task Start_window_lists_recent_and_creates_a_project_from_a_template()
    {
        SampleSolution.EnsureConfigIsolated();
        var root = Path.Combine(Path.GetTempPath(), "vsl-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Shop"));
        var existing = Path.Combine(root, "Shop", "Shop.slnx");
        File.WriteAllText(existing, "<Solution />");
        var missing = Path.Combine(root, "Gone", "Gone.sln");
        var now = DateTime.Now;
        var settings = new VSL.Services.Settings { NewProjectLocation = root };
        settings.RecentSolutions.AddRange([existing, missing, "/home/user/source/repos/Api/Api.csproj"]);
        settings.RecentOpened[existing] = now.AddMinutes(-5);
        settings.RecentOpened[missing] = now.AddDays(-3);
        settings.PinnedSolutions.Add("/home/user/source/repos/Api/Api.csproj");

        var vm = new StartViewModel(settings);
        var window = new StartWindow { DataContext = vm, Width = 1060, Height = 700 };
        string? chosen = null;
        window.Finished += path => chosen = path;
        window.Show();
        Assert.Equal(["Pinned", "Today", "This week"], vm.RecentGroups.Select(g => g.Header));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "start-home");

        // A recent entry that no longer exists explains itself instead of opening.
        vm.OpenRecentCommand.Execute(vm.RecentGroups[2].Items[0]);
        Assert.Contains("could not be found", vm.Message);
        Assert.Null(chosen);

        // Create a new project: pick a template...
        vm.ShowNewProjectCommand.Execute(null);
        await WaitUntil(() => !vm.TemplatesLoading && vm.Templates.Count > 0, TimeSpan.FromMinutes(1));
        Assert.Contains("C#", vm.Languages);
        Assert.Equal("console", vm.Templates[0].Template.ShortName);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "start-templates");
        vm.TemplateSearch = "console";
        vm.LanguageFilter = "C#";
        vm.SelectedTemplate = Assert.Single(vm.Templates);
        Assert.Equal("Console App", vm.SelectedTemplate.Name);
        vm.NextCommand.Execute(null);

        // ...configure it (the solution name follows the project name until edited)...
        Assert.True(vm.IsConfigure);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal("ConsoleApp1", vm.ProjectName);
        vm.ProjectName = "HelloVsl";
        Assert.Equal("HelloVsl", vm.SolutionName);
        Assert.Contains(Path.Combine(root, "HelloVsl", "HelloVsl"), vm.CreatedInText);
        Assert.Null(vm.ConfigureError);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "start-configure");
        vm.NextCommand.Execute(null);

        // ...and choose the additional information VS offers for it.
        Assert.True(vm.IsAdditionalInformation);
        var framework = vm.Options.First(o => o.Label == "Framework");
        Assert.StartsWith(".NET 10.0", framework.SelectedChoice!.Label);
        var programMain = vm.Options.First(o => o.Option.Symbol == "UseProgramMain");
        Assert.Equal("Do not use top-level statements", programMain.Label);
        programMain.IsChecked = true;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "start-additional");
        Assert.True(vm.CreateCommand.CanExecute(null));
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Null(vm.Message);

        Assert.Equal(Path.Combine(root, "HelloVsl", "HelloVsl.slnx"), chosen);
        Assert.Contains("static void Main", File.ReadAllText(Path.Combine(root, "HelloVsl", "HelloVsl", "Program.cs")));
        Assert.Equal(vm.Templates[0].Template.Identity, settings.RecentTemplates[0]);

        // The start window can also clone: a local repository holding a solution opens right away.
        var origin = Path.Combine(root, "origin");
        Directory.CreateDirectory(origin);
        File.WriteAllText(Path.Combine(origin, "Repo.slnx"), "<Solution />");
        TempRepo.Git(origin, "init", "-q", "-b", "main");
        TempRepo.Git(origin, "add", "-A");
        TempRepo.Git(origin, "-c", "user.name=VSL Tests", "-c", "user.email=tests@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "initial");
        var cloneVm = new StartViewModel(settings) { Page = StartPage.Clone };
        string? cloned = null;
        cloneVm.SolutionChosen += path => cloned = path;
        cloneVm.CloneUrl = origin;
        Assert.Equal(Path.Combine(StartViewModel.DefaultReposDirectory, "origin"), cloneVm.ClonePath);
        cloneVm.ClonePath = Path.Combine(root, "clones", "Repo");
        await cloneVm.CloneCommand.ExecuteAsync(null);
        Assert.Null(cloneVm.Message);
        Assert.Equal(Path.Combine(root, "clones", "Repo", "Repo.slnx"), cloned);

        // Closing the start window (X) continues without code, exactly once.
        var closing = new StartWindow { DataContext = new StartViewModel(settings) };
        var finished = new List<string?>();
        closing.Finished += finished.Add;
        closing.Show();
        closing.Close();
        Assert.Equal([null], finished);
    }

    [AvaloniaFact]
    public async Task Opens_a_project_without_a_solution()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        var project = SampleSolution.PathOf(sln, "Web/Web.csproj");
        await vm.OpenSolutionAsync(project);

        // VS shows the project under an implicit solution of the same name.
        Assert.True(vm.Solution!.IsProjectOnly);
        Assert.Equal("Web", Assert.Single(vm.Solution.Projects).Name);
        Assert.Equal("Solution 'Web'", vm.ExplorerRoots[0].Name);
        Assert.Equal(project, vm.Settings.RecentSolutions[0]);

        // Roslyn loads it (project/open) and IntelliSense works.
        var host = vm.OpenDocument(SampleSolution.PathOf(sln, "Web/Program.cs"))!.Host;
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));
        Assert.Equal("IntelliSense: ready", vm.ServerStatus);
        host.Document.Insert(host.Document.TextLength, "\nint broken = \"text\";\n");
        await WaitUntil(() => host.Diagnostics.Any(d => d.Code == "CS0029"), TimeSpan.FromSeconds(60));
        Assert.Contains(host.Diagnostics, d => d.Code == "CS0029");
        while (host.Editor.CanUndo)
            host.Editor.Undo();
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Claude_panel_runs_conversations_with_tools_and_approvals()
    {
        var (dir, log) = FakeClaude.Workspace();
        var settings = new VSL.Services.Settings();
        var context = new ClaudeEditorContext(Path.Combine(dir, "Program.cs"), 1, 1, "Console.WriteLine(\"Hello\");");
        var vm = new ClaudeViewModel(settings, () => dir, () => context, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
        var window = new Avalonia.Controls.Window { Width = 560, Height = 980, Content = new ClaudeView { DataContext = vm } };
        window.Show();

        // Showing the panel starts the CLI: commands and models arrive before the first prompt.
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));
        Assert.Equal(["Default (recommended)", "Haiku"], vm.Models.Select(m => m.Label));
        Assert.Equal("Claude Pro · tests@example.invalid", vm.Account);
        Snapshot(window, "claude-empty");
        vm.Prompt = "/co";
        Assert.Equal("/compact", Assert.Single(vm.Suggestions).Label);
        vm.ApplySuggestionCommand.Execute(vm.Suggestions[0]);
        Assert.Equal("/compact ", vm.Prompt);
        vm.Prompt = "look at @Prog";
        vm.ApplySuggestionCommand.Execute(Assert.Single(vm.Suggestions, s => s.Label == "Program.cs"));
        Assert.Equal("look at @Program.cs ", vm.Prompt);

        // A question: thinking, tool calls with their outcome, a todo list and a Markdown answer.
        vm.RefreshContext();
        Assert.Equal("Program.cs:1", vm.ContextLabel);
        vm.Prompt = "Explain this program";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Program.cs:1", Assert.IsType<UserChatItem>(vm.Items[0]).Context);
        Assert.Contains("look at the file", vm.Items.OfType<ThinkingChatItem>().Single().Text);
        var read = vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "Read");
        Assert.Equal(("Program.cs", "Read 2 lines", true), (read.Target, read.Summary, read.IsDone));
        Assert.Equal(["Read Program.cs", "Explain it"], vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "TodoWrite").Todos.Select(t => t.Content));
        Assert.StartsWith("## Answer", vm.Items.OfType<TextChatItem>().Last().Text);
        var prompt = FakeClaude.Log(log).Last(e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "user").Data;
        var content = prompt.GetProperty("message").GetProperty("content");
        Assert.Equal("Explain this program", content[0].GetProperty("text").GetString());
        Assert.Contains("selected lines 1-1", content[1].GetProperty("text").GetString());
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "claude-answer");

        // Links in answers are clickable: a file reference opens the file at its line.
        (string Path, int? Line)? opened = null;
        vm.OpenFileRequested += (path, line) => opened = (path, line);
        var answerBlock = window.GetVisualDescendants().OfType<Avalonia.Controls.SelectableTextBlock>()
            .Single(b => b.Tag is IReadOnlyList<MdLink> l && l.Any(x => x.Target == "Program.cs#L1"));
        var fileLink = ((IReadOnlyList<MdLink>)answerBlock.Tag!).First(x => x.Target == "Program.cs#L1");
        var at = answerBlock.TextLayout.HitTestTextPosition(fileLink.Start + 1);
        var click = answerBlock.TranslatePoint(new Point(at.X + 2, at.Y + at.Height / 2), window)!.Value;
        Assert.Equal(fileLink, MarkdownView.LinkAt(answerBlock, new Point(at.X + 2, at.Y + at.Height / 2)));
        window.MouseDown(click, Avalonia.Input.MouseButton.Left);
        window.MouseUp(click, Avalonia.Input.MouseButton.Left);
        Assert.Equal((Path.Combine(dir, "Program.cs"), 1), opened);

        // Images can be attached (and pasted or dropped); other files are mentioned so Claude reads them.
        Assert.NotNull(vm.AddImage("notes.bin", [1, 2, 3]));
        Assert.Contains("not a PNG", vm.AttachmentError);
        Assert.Null(vm.AddImage("red.png", RedPng()));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "the secret word is pineapple\n");
        vm.AddFiles([Path.Combine(dir, "notes.txt")]);
        Assert.Equal("@notes.txt ", vm.Prompt);
        Assert.Single(vm.Attachments);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "claude-attachment");
        vm.Prompt = "describe this";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Empty(vm.Attachments);
        var withImage = vm.Items.OfType<UserChatItem>().Last();
        Assert.NotNull(Assert.Single(withImage.Images).Thumbnail);
        Assert.Equal("I see 1 image (image/png).", vm.Items.OfType<TextChatItem>().Last().Text);

        // A subagent's steps show under its Agent call; its approvals say they come from the subagent.
        var userBubbles = vm.Items.OfType<UserChatItem>().Count();
        vm.Prompt = "use an agent";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(), TimeSpan.FromSeconds(10));
        var agent = vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "Agent");
        Assert.Equal(("Agent", "Find the secret word"), (agent.Title, agent.Target));
        Assert.Equal(["Search", "Read", "Bash"], agent.Children.Select(c => c.Title));
        Assert.True(agent.Children[2].IsRunning);
        Assert.Equal("3 tool uses", agent.Summary);
        var subagentCard = vm.Items.OfType<PermissionChatItem>().Single();
        Assert.Equal("A subagent asks: Allow Claude to run this command?", subagentCard.Question);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "claude-agent");
        await vm.AllowCommand.ExecuteAsync(subagentCard);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Done (3 tool uses)", agent.Summary);
        Assert.All(agent.Children, c => Assert.True(c.IsDone));
        Assert.Equal(userBubbles + 1, vm.Items.OfType<UserChatItem>().Count());
        Assert.Equal("The subagent found **pineapple**.", vm.Items.OfType<TextChatItem>().Last().Text);
        vm.Items.Clear();

        // An edit needs approval: the card shows the diff; allowing it for the session switches to auto-accept.
        var filesChanged = 0;
        vm.FilesChanged += () => filesChanged++;
        vm.Prompt = "write a hello file";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(), TimeSpan.FromSeconds(10));
        var card = vm.Items.OfType<PermissionChatItem>().Single();
        Assert.Equal("Allow Claude to create hello.txt?", card.Question);
        Assert.Equal("Yes, allow all edits during this session", card.SessionLabel);
        Assert.Equal(["hi", "there"], card.Diff.Select(d => d.Text));
        var write = vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "Write");
        Assert.True(write.IsRunning);
        Assert.False(write.ShowDiff);
        Assert.StartsWith("Waiting for you", vm.StatusText);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "claude-permission");
        await vm.AllowForSessionCommand.ExecuteAsync(card);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("hi\nthere\n", File.ReadAllText(Path.Combine(dir, "hello.txt")));
        Assert.Equal("acceptEdits", vm.PermissionMode);
        Assert.Equal("acceptEdits", settings.ClaudePermissionMode);
        Assert.Equal(1, filesChanged);
        var decision = FakeClaude.LastDecision(log);
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.Equal("acceptEdits", decision.GetProperty("updatedPermissions")[0].GetProperty("mode").GetString());

        // Saying no with a reason passes the reason to Claude.
        vm.Prompt = "run bash";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Count() == 2, TimeSpan.FromSeconds(10));
        var bash = vm.Items.OfType<PermissionChatItem>().Last();
        Assert.Equal(("Allow Claude to run this command?", "echo hi"), (bash.Question, bash.Code));
        Assert.Equal("Yes, and don't ask again for Bash(echo:*) in this project", bash.SessionLabel);
        bash.Feedback = "use dotnet build instead";
        await vm.DenyCommand.ExecuteAsync(bash);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        decision = FakeClaude.LastDecision(log);
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Contains("use dotnet build instead", decision.GetProperty("message").GetString());
        var bashTool = vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "Bash");
        Assert.Equal((true, "Denied by you"), (bashTool.IsError, bashTool.Summary));
        Assert.True(write.ShowDiff);

        // Clarifying questions come back as answers keyed by question.
        vm.Prompt = "ask me something";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<QuestionChatItem>().Any(), TimeSpan.FromSeconds(10));
        var question = vm.Items.OfType<QuestionChatItem>().Single();
        await vm.SubmitAnswersCommand.ExecuteAsync(question);
        Assert.Equal("Answer each question first.", question.Outcome);
        question.Questions[0].Options[0].IsSelected = true;
        question.Questions[0].Options[1].IsSelected = true;
        Assert.False(question.Questions[0].Options[0].IsSelected);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "claude-question");
        await vm.SubmitAnswersCommand.ExecuteAsync(question);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("You picked SQLite.", vm.Items.OfType<TextChatItem>().Last().Text);

        // A plan is approved with the edit mode chosen on the card.
        vm.Prompt = "plan it";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<PlanChatItem>().Any(), TimeSpan.FromSeconds(10));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "claude-plan");
        await vm.ApprovePlanManuallyCommand.ExecuteAsync(vm.Items.OfType<PlanChatItem>().Single());
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("default", vm.PermissionMode);
        Assert.Equal("default", FakeClaude.LastDecision(log).GetProperty("updatedPermissions")[0].GetProperty("mode").GetString());
        Assert.Equal("Plan approved.", vm.Items.OfType<TextChatItem>().Last().Text);

        // Changing the mode while the CLI runs is sent to it.
        vm.CycleModeCommand.Execute(null);
        Assert.Equal("Edit automatically", vm.ModeLabel);
        await WaitUntil(() => FakeClaude.Log(log).Any(e => e.Kind == "in" && e.Data.ToString().Contains("\"set_permission_mode\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));

        // Stop interrupts a running turn.
        vm.Prompt = "slow job";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<TextChatItem>().LastOrDefault()?.Text == "Working on it", TimeSpan.FromSeconds(10));
        Assert.True(vm.IsRunning);
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Interrupted.", vm.Items.OfType<NoticeChatItem>().Last().Text);

        // Past conversations of the folder can be reopened and continued (--resume).
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        FakeClaude.WriteTranscript(dir, "old-session");
        vm.ToggleHistoryCommand.Execute(null);
        Assert.True(vm.IsHistoryOpen);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "claude-history");
        var past = Assert.Single(vm.History);
        Assert.Equal("Fix the build", past.Title);
        await vm.ResumeConversationCommand.ExecuteAsync(past);
        Assert.Equal("Fix the build", Assert.IsType<UserChatItem>(vm.Items[0]).Text);
        Assert.Equal("Fixed.", vm.Items.OfType<TextChatItem>().Last().Text);
        Assert.True(vm.Items.OfType<ToolChatItem>().Single().IsDone);
        vm.Prompt = "thanks";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        var argv = FakeClaude.Log(log).Last(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("old-session", argv[argv.IndexOf("--resume") + 1]);
        Assert.Equal("acceptEdits", argv[argv.IndexOf("--permission-mode") + 1]);
        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Claude_prompt_pastes_images_and_text()
    {
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new VSL.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable);
        var view = new ClaudeView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 480, Height = 700, Content = view };
        window.Show();
        var prompt = view.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().Single(t => t.Name == "PromptBox");
        prompt.Focus();

        // An image on the clipboard becomes an attachment...
        // The headless clipboard hands back this very bitmap, so it stays alive until the paste is done.
        using var image = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(RedPng()));
        await Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(window.Clipboard!, image);
        Assert.True(prompt.IsFocused);
        window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, "v");
        await WaitUntil(() => vm.Attachments.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(("pasted image 1.png", "image/png"), (vm.Attachments[0].Name, vm.Attachments[0].MediaType));

        // ...and text is pasted as text.
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(window.Clipboard!, "explain this");
        window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, "v");
        await WaitUntil(() => vm.Prompt == "explain this", TimeSpan.FromSeconds(5));
        Assert.Equal("explain this", vm.Prompt);
        Assert.Single(vm.Attachments);
        window.Close();
        await vm.DisposeAsync();
    }

    /// <summary>A small red PNG, encoded by Avalonia.</summary>
    static byte[] RedPng()
    {
        using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(24, 16), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[24 * 16 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)30, (byte)30, (byte)220, (byte)255);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    [AvaloniaFact]
    public async Task Claude_window_opens_from_the_ide_with_ctrl_esc()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 900 };
        window.Show();
        window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Escape, null);
        Assert.True(window.IsToolWindowShowing(ToolWindows.Claude));
        // The panel starts the CLI (the fake here) as soon as it is shown.
        await WaitUntil(() => vm.Claude.Commands.Count > 0, TimeSpan.FromSeconds(10));
        Assert.Contains(vm.Claude.Commands, c => c.Name == "compact");
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "claude-in-ide");
        await vm.ShutdownClaudeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Editor_zooms_with_ctrl_wheel_and_shortcuts()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var doc = vm.OpenDocument(SampleSolution.PathOf(sln, "Web/Program.cs"))!;
        var host = doc.Host;
        host.SetZoom(1.0);
        await WaitUntil(() => host.Editor.IsVisible && host.Editor.Bounds.Width > 0, TimeSpan.FromSeconds(5));
        Assert.Equal("100 %", doc.ZoomText);
        Assert.Equal("No issues found", doc.IssuesText);
        Assert.Equal("Ok", doc.IssuesIcon);

        // Ctrl + mouse wheel over the editor, like VS.
        var point = host.Editor.TranslatePoint(new Avalonia.Point(200, 100), window)!.Value;
        window.MouseWheel(point, new Avalonia.Vector(0, 1), Avalonia.Input.RawInputModifiers.Control);
        window.MouseWheel(point, new Avalonia.Vector(0, 1), Avalonia.Input.RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.2, host.Zoom, 3);
        Assert.Equal("120 %", doc.ZoomText);
        Assert.Equal(VSL.Editor.EditorHost.BaseFontSize * 1.2, host.Editor.FontSize, 3);
        // A plain wheel still scrolls instead of zooming.
        window.MouseWheel(point, new Avalonia.Vector(0, -1), Avalonia.Input.RawInputModifiers.None);
        Assert.Equal(1.2, host.Zoom, 3);

        // Keyboard: Ctrl+Shift+, zooms out, Ctrl+0 resets.
        host.Editor.TextArea.Focus();
        window.KeyPress(Avalonia.Input.Key.OemComma, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift,
            Avalonia.Input.PhysicalKey.Comma, ",");
        Assert.Equal(1.1, host.Zoom, 3);
        window.KeyPress(Avalonia.Input.Key.D0, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Digit0, "0");
        Assert.Equal(1.0, host.Zoom, 3);

        // The last zoom carries over to newly opened documents and is remembered in settings.
        host.SetZoom(1.5);
        Assert.Equal(1.5, vm.Settings.EditorZoom, 3);
        var other = vm.OpenDocument(SampleSolution.PathOf(sln, "Components/Greeting.razor.cs"))!;
        Assert.Equal(1.5, other.Host.Zoom, 3);
        Snapshot(window, "editor-zoom");

        host.SetZoom(1.0);
        other.Host.SetZoom(1.0);
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Terminal_runs_the_shell_in_the_solution_folder()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        // Ctrl+` opens the Terminal tool window; the shell starts once it has a size.
        window.KeyPress(Avalonia.Input.Key.OemTilde, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Backquote, "`");
        Assert.True(window.IsToolWindowShowing(ToolWindows.Terminal));
        await WaitUntil(() => vm.Terminal.IsAlive, TimeSpan.FromSeconds(15));
        Assert.True(vm.Terminal.IsAlive);
        await WaitUntil(() => vm.Terminal.Control.IsFocused, TimeSpan.FromSeconds(5));

        // Typed through the real keyboard path; the shell evaluates it.
        window.KeyTextInput("echo VSL_TERM_$((40+2)); pwd; dotnet --version");
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "\r");
        await WaitUntil(() => vm.Terminal.ReadText().Contains("VSL_TERM_42") && vm.Terminal.ReadText().Contains("10.0."), TimeSpan.FromSeconds(30));
        var text = vm.Terminal.ReadText();
        Assert.Contains("VSL_TERM_42", text);
        Assert.Contains(Path.GetDirectoryName(sln)!, text);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\n)10\.0\.\d+"), "terminal text:\n" + text);

        // Ctrl+R inside the terminal is bash's reverse search, not VS's Ctrl+R, A "Run All Tests" chord.
        window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.R, "r");
        window.KeyPress(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.A, "a");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsTesting);
        Assert.Empty(vm.TestRoots);
        await WaitUntil(() => vm.Terminal.ReadText().Contains("reverse-i-search"), TimeSpan.FromSeconds(10));
        Assert.Contains("reverse-i-search", vm.Terminal.ReadText());
        window.KeyPress(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, "c");
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
        Snapshot(window, "terminal");

        await vm.KillTerminalCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await WaitUntil(() => !vm.Terminal.IsAlive, TimeSpan.FromSeconds(10));
        Assert.False(vm.Terminal.IsAlive);
        Assert.Contains("[Terminal killed", vm.Terminal.ReadText());

        Assert.True(await vm.CloseSolutionCoreAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await vm.ShutdownTerminalAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public void Icon_gallery_renders_every_icon()
    {
        var panel = new Avalonia.Controls.WrapPanel { Margin = new Avalonia.Thickness(8) };
        foreach (var kind in VsIcon.AllKinds)
            panel.Children.Add(new Avalonia.Controls.StackPanel
            {
                Width = 96,
                Margin = new Avalonia.Thickness(4),
                Spacing = 4,
                Children =
                {
                    new Avalonia.Controls.StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        Children = { new VsIcon { Kind = kind }, new VsIcon { Kind = kind, Width = 32, Height = 32 } },
                    },
                    new Avalonia.Controls.TextBlock { Text = kind, FontSize = 10, Foreground = Avalonia.Media.Brushes.Gray },
                },
            });
        var window = new Avalonia.Controls.Window
        {
            Width = 1100,
            Height = 560,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D2D30")),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C5C5C5")),
            Content = panel,
        };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(SampleSolution.ArtifactsDir, "icons.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Assert.True(File.Exists(Path.Combine(SampleSolution.ArtifactsDir, "icons.png")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Document_tab_context_menu_commands()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        string[] files = ["Web/Program.cs", "Components/Home.razor", "Components/Greeting.razor", "Components/Greeting.razor.cs"];
        var docs = files.Select(f => vm.OpenDocument(SampleSolution.PathOf(sln, f))!).ToList();
        string Names() => string.Join(",", vm.Documents.Select(d => d.Title));

        // Pinning moves the tab to the left edge.
        vm.TogglePinCommand.Execute(docs[2]);
        Assert.True(docs[2].IsPinned);
        Assert.Equal("Greeting.razor,Program.cs,Home.razor,Greeting.razor.cs", Names());

        // Right-clicking a tab header opens VS's tab menu.
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        var header = window.GetVisualDescendants().OfType<Avalonia.Controls.StackPanel>().First(p => p.DataContext == docs[0]);
        var args = new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Avalonia.Controls.Control.ContextRequestedEvent };
        header.RaiseEvent(args);
        Assert.True(args.Handled);

        // "Close All Tabs to the Right" of Program.cs keeps pinned tabs and those on the left.
        await vm.CloseTabsToRightCommand.ExecuteAsync(docs[0]);
        Assert.Equal("Greeting.razor,Program.cs", Names());

        // "Close All But Pinned" leaves only the pinned tab; "Close All Documents" closes it too.
        await vm.CloseAllButPinnedCommand.ExecuteAsync(null);
        Assert.Equal("Greeting.razor", Names());
        vm.OpenDocument(SampleSolution.PathOf(sln, "Web/Program.cs"));
        await vm.CloseAllButThisCommand.ExecuteAsync(vm.Editors.Single(d => d.FileName == "Program.cs"));
        Assert.Equal("Program.cs", Names());
        await vm.CloseAllDocumentsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Documents);

        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task NuGet_manager_browses_installs_and_uninstalls()
    {
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            var online = false;
            try
            {
                online = (await probe.GetAsync("https://api.nuget.org/v3/index.json", TestContext.Current.CancellationToken)).IsSuccessStatusCode;
            }
            catch (Exception)
            {
            }
            Assert.SkipUnless(online, "nuget.org is not reachable");
        }
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var components = vm.Solution!.Projects.Single(p => p.Name == "Components");
        var csproj = File.ReadAllText(components.FilePath);

        await vm.ManageNuGetPackagesCommand.ExecuteAsync(components);
        var nuget = Assert.IsType<NuGetViewModel>(vm.ActiveDocument);
        Assert.Equal("NuGet: Components", nuget.Title);
        Assert.Empty(nuget.Packages.Where(p => p.IsInstalled));

        // Browse: search, pick a version, install.
        nuget.SearchText = "Humanizer.Core";
        await nuget.SearchCommand.ExecuteAsync(null);
        var humanizer = nuget.Packages.First(p => p.Id == "Humanizer.Core");
        nuget.SelectedPackage = humanizer;
        await WaitUntil(() => nuget.Versions.Contains("2.14.1"), TimeSpan.FromSeconds(60));
        Assert.Contains("2.14.1", nuget.Versions);
        Assert.False(string.IsNullOrEmpty(humanizer.Description));
        nuget.SelectedVersion = "2.14.1";
        Assert.Equal("Install", nuget.InstallButtonText);
        await nuget.InstallCommand.ExecuteAsync(null);
        await WaitUntil(() => !nuget.IsBusy, TimeSpan.FromSeconds(60));
        Assert.Contains("Humanizer.Core", File.ReadAllText(components.FilePath));
        Assert.Equal("2.14.1", nuget.Packages.First(p => p.Id == "Humanizer.Core").InstalledVersion);
        Assert.True(nuget.UpdateCount >= 1);
        // Solution Explorer's Dependencies now list the package.
        Assert.Contains(vm.Solution!.Projects.Single(p => p.Name == "Components").Packages, p => p.Name == "Humanizer.Core");

        // Installed and Updates tabs.
        nuget.Tab = NuGetTab.Installed;
        await WaitUntil(() => !nuget.IsBusy && nuget.Packages.Any(p => p.Id == "Humanizer.Core"), TimeSpan.FromSeconds(30));
        nuget.Tab = NuGetTab.Updates;
        await WaitUntil(() => !nuget.IsBusy && nuget.Packages.Any(p => p.Id == "Humanizer.Core"), TimeSpan.FromSeconds(30));
        Assert.True(nuget.Packages.Single(p => p.Id == "Humanizer.Core").HasUpdate);
        await WaitUntil(() => nuget.Versions.Count > 0 && nuget.SelectedPackage?.Description is not null, TimeSpan.FromSeconds(60));
        Assert.Equal("Update", nuget.InstallButtonText);
        Snapshot(window, "nuget");

        // Uninstall restores the project file.
        nuget.SelectedPackage = nuget.Packages.Single(p => p.Id == "Humanizer.Core");
        await nuget.UninstallCommand.ExecuteAsync(null);
        await WaitUntil(() => !nuget.IsBusy, TimeSpan.FromSeconds(60));
        Assert.DoesNotContain("Humanizer.Core", File.ReadAllText(components.FilePath));
        File.WriteAllText(components.FilePath, csproj);

        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Debug_test_stops_at_breakpoint_in_test_code()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        await vm.RefreshTestsCommand.ExecuteAsync(null);
        var test = vm.TestRoots.Single().Children.Single().Children.Single().Children.Single(n => n.Name == "Default_name_is_world");
        var source = SampleSolution.PathOf(sln, "Tests/GreetingTests.cs");
        var doc = vm.OpenDocument(source)!;
        doc.Host.ToggleBreakpoint(9);

        vm.SelectedTest = test;
        var run = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromSeconds(90));
        Assert.True(vm.Mode == DebugMode.Break, "did not break; debug output:\n" + vm.DebugOutput.Document.Text);
        Assert.Equal(source, vm.CallStack[0].SourcePath);
        Assert.Equal(9, vm.CallStack[0].Line);
        await WaitUntil(() => doc.Host.ExecutionLine == 9, TimeSpan.FromSeconds(10));
        Assert.Equal(9, doc.Host.ExecutionLine);
        Snapshot(window, "debug-test");

        // F5 continues; the test finishes green and debugging ends.
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await run.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(test.State == TestState.Passed, $"state {test.State}; summary: {vm.TestSummary}\nbuild/vstest:\n{vm.BuildOutput.Document.Text[^Math.Min(1500, vm.BuildOutput.Document.TextLength)..]}\ndebug:\n{vm.DebugOutput.Document.Text}");
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        Assert.Equal(DebugMode.Design, vm.Mode);

        doc.Host.ToggleBreakpoint(9);
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Tool_windows_dock_by_dragging_and_context_menu()
    {
        var vm = NewViewModel();
        vm.Settings.ToolLayout.Clear();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Equal(DockSide.Bottom, window.SideOf(ToolWindows.ErrorList));
        var leftDock = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "LeftDock");
        Assert.False(leftDock.IsVisible);

        // Drag the Error List tab onto the "Dock Left" guide.
        var tab = window.GetVisualDescendants().OfType<Avalonia.Controls.TabItem>().Single(t => (string?)t.Tag == ToolWindows.ErrorList);
        var start = tab.TranslatePoint(new Avalonia.Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, Avalonia.Input.MouseButton.Left);
        window.MouseMove(start + new Avalonia.Point(20, -20), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        var zone = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "DockZoneLeft");
        Assert.True(zone.IsEffectivelyVisible);
        var target = zone.TranslatePoint(new Avalonia.Point(zone.Bounds.Width / 2, zone.Bounds.Height / 2), window)!.Value;
        window.MouseMove(target, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        Assert.Contains("hot", zone.Classes);
        Snapshot(window, "dock-guides");
        window.MouseUp(target, Avalonia.Input.MouseButton.Left);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));

        Assert.Equal(DockSide.Left, window.SideOf(ToolWindows.ErrorList));
        Assert.True(leftDock.IsVisible);
        Assert.Equal("Left", vm.Settings.ToolLayout[ToolWindows.ErrorList]);
        Snapshot(window, "docked-left");

        // ShowToolWindow finds it in its new group; the context menu's Dock Right moves it again.
        vm.ShowToolWindow(ToolWindows.ErrorList);
        window.MoveToolWindow(ToolWindows.ErrorList, DockSide.Right);
        Assert.Equal(DockSide.Right, window.SideOf(ToolWindows.ErrorList));
        Assert.False(leftDock.IsVisible);

        // A new window restores the saved layout; Reset Window Layout puts everything back.
        var second = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        second.Show();
        Assert.Equal(DockSide.Right, second.SideOf(ToolWindows.ErrorList));
        second.ResetWindowLayout();
        Assert.Equal(DockSide.Bottom, second.SideOf(ToolWindows.ErrorList));
        Assert.Empty(vm.Settings.ToolLayout);
        second.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Start_window_renders_without_solution()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Assert.True(File.Exists(Snapshot(window, "start-window")));
        Assert.False(vm.HasSolution);
    }

    [AvaloniaFact]
    public async Task Opens_blazor_solution_with_intellisense()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();

        await vm.OpenSolutionAsync(sln);
        Assert.NotNull(vm.Solution);
        Assert.Equal("Web", vm.StartupProject?.Name);
        Assert.Equal(["http"], vm.LaunchProfiles);

        // Expand the component library in Solution Explorer like a user would.
        var root = vm.ExplorerRoots[0];
        root.Children.First(n => n.Name == "Components").IsExpanded = true;
        root.Children.First(n => n.Name == "Web").IsExpanded = true;

        var razorDoc = vm.OpenDocument(SampleSolution.PathOf(sln, "Components/Home.razor"))!;
        var csDoc = vm.OpenDocument(SampleSolution.PathOf(sln, "Components/Greeting.razor.cs"))!;
        Assert.Equal(csDoc, vm.ActiveDocument);

        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));
        Assert.Equal("IntelliSense: ready", vm.ServerStatus);

        // Type a broken statement through the editor's input pipeline and wait for the squiggle.
        var host = csDoc.Host;
        var text = host.Document.Text;
        host.Editor.CaretOffset = text.IndexOf('{', text.IndexOf("class ", StringComparison.Ordinal)) + 1;
        host.Editor.TextArea.PerformTextInput("\n    int __bad = \"abc\";");
        await WaitUntil(() => host.Diagnostics.Any(d => d.Code == "CS0029"), TimeSpan.FromSeconds(60));
        Assert.Contains(host.Diagnostics, d => d.Code == "CS0029");
        Assert.Contains(vm.Errors, e => e.Code == "CS0029" && e.Project == "Components");
        Assert.True(csDoc.IsDirty);

        // Member completion pops up after typing "System.Console.".
        host.Editor.TextArea.PerformTextInput("\n    void __M() { System.Console.");
        await WaitUntil(() => host.CompletionItemCount > 0, TimeSpan.FromSeconds(30));
        Assert.Contains("WriteLine", host.CompletionLabels);
        Snapshot(window, "csharp-completion");
        host.CloseCompletion();

        // Undo everything so the document is clean again (tests never save).
        while (host.Editor.CanUndo)
            host.Editor.Undo();
        Assert.False(csDoc.IsDirty);

        vm.ActiveDocument = razorDoc;
        await WaitUntil(() => false, TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(Snapshot(window, "razor-editor")));

        Assert.True(await vm.CloseSolutionCoreAsync());
    }
}
