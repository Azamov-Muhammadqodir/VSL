using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VSL.ViewModels;
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
