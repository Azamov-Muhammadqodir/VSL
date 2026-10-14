using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    static string Snapshot(MainWindow window, string name)
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
        var vm = new MainViewModel();
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
            Assert.Equal(0, vm.BottomTabIndex);
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
        var vm = new MainViewModel();
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
        var vm = new MainViewModel();
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
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.RightTabIndex);
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
        await WaitUntil(() => vm.ActiveDocument?.FileName == "GreetingTests.cs", TimeSpan.FromSeconds(5));
        await WaitUntil(() => vm.ActiveDocument!.Host.CaretPosition.Line == 17, TimeSpan.FromSeconds(5));
        Assert.Equal(17, vm.ActiveDocument!.Host.CaretPosition.Line);

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

        var vm = new MainViewModel();
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
        await vm.OpenDiffCommand.ExecuteAsync(vm.UnstagedChanges[0]);
        Assert.Equal("Program.cs.diff", vm.ActiveDocument!.FileName);
        Assert.Contains("+// top", vm.ActiveDocument.Host.Document.Text);
        Assert.True(vm.ActiveDocument.Host.Editor.IsReadOnly);
        vm.RightTabIndex = MainViewModel.GitChangesTab;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "git-changes");

        await vm.SwitchBranchCommand.ExecuteAsync("main");
        Assert.Equal("main", vm.GitBranch);
        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.False(vm.HasGit);
    }

    [AvaloniaFact]
    public async Task Start_window_renders_without_solution()
    {
        var vm = new MainViewModel();
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
        var vm = new MainViewModel();
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
