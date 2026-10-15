using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using VSL.Lsp;
using VSL.Testing;

namespace VSL.ViewModels;

/// <summary>Test Explorer: discovery and runs through vstest, results in a Project/Namespace/Class/Method tree.</summary>
public partial class MainViewModel
{
    readonly Dictionary<Guid, TestNode> _testCases = [];
    readonly Dictionary<Guid, TestCase> _discovered = [];
    List<Guid> _lastRun = [];
    VsTestHost? _testHost;
    CancellationTokenSource? _testCts;

    public ObservableCollection<TestNode> TestRoots { get; } = [];

    [ObservableProperty]
    public partial TestNode? SelectedTest { get; set; }

    [ObservableProperty]
    public partial string TestSummary { get; set; } = "Build the solution to discover tests.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand),
        nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand), nameof(CancelTestRunCommand),
        nameof(DebugAllTestsCommand), nameof(DebugSelectedTestsCommand))]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial int PassedCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    [ObservableProperty]
    public partial int SkippedCount { get; set; }


    bool CanRunTests => Solution is { } s && s.Projects.Any(p => p.IsTestProject) && !IsTesting && !IsBuilding && Mode == DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunAllTests() => RunTestsAsync(null);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunFailedTests() => RunTestsAsync(_testCases.Where(kv => kv.Value.State == TestState.Failed).Select(kv => kv.Key).ToList());

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunSelectedTests() => RunTestsAsync(SelectedTest?.CaseIds().ToList() ?? []);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RepeatLastTestRun() => RunTestsAsync(_lastRun.Count > 0 ? _lastRun : null);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task DebugAllTests() => RunTestsAsync(null, debug: true);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task DebugSelectedTests() => RunTestsAsync(SelectedTest?.CaseIds().ToList() ?? [], debug: true);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    async Task RefreshTests()
    {
        ShowToolWindow(ToolWindows.TestExplorer);
        IsTesting = true;
        try
        {
            await DiscoverTestsAsync();
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    void CancelTestRun() => _testCts?.Cancel();

    /// <summary>Builds, (re)discovers and runs the given tests (null = all), streaming results into the tree.</summary>
    async Task RunTestsAsync(IReadOnlyList<Guid>? ids, bool debug = false)
    {
        ShowToolWindow(ToolWindows.TestExplorer);
        IsTesting = true;
        try
        {
            if (!await DiscoverTestsAsync() || _testHost is not { } host)
                return;
            var cases = (ids is null ? _discovered.Values : ids.Where(_discovered.ContainsKey).Select(id => _discovered[id])).ToList();
            if (cases.Count == 0)
            {
                TestSummary = "No tests to run.";
                return;
            }
            _lastRun = cases.Select(c => c.Id).ToList();
            foreach (var c in cases)
                _testCases[c.Id].SetResult(TestState.NotRun, TimeSpan.Zero, null, null, null);
            SetStatus($"Running {cases.Count} test(s)...", StatusKind.Busy);
            var cts = _testCts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            void OnResult(TestResultInfo r) => Dispatcher.UIThread.Post(() =>
            {
                if (_testCases.TryGetValue(r.Id, out var node))
                    node.SetResult(r.State, r.Duration, r.ErrorMessage, r.StackTrace, r.Output);
            });
            if (debug)
            {
                // vstest starts the test host and asks us to attach before any test runs, so breakpoints hit.
                DebugOutput.Clear();
                var launcher = new DebuggingTestHostLauncher(
                    pid => Dispatcher.UIThread.InvokeAsync(() => AttachTestDebuggerAsync(pid)),
                    DebugOutput.Append);
                await host.DebugAsync(cases, launcher, OnResult, cts.Token);
            }
            else
            {
                await host.RunAsync(cases, OnResult, cts.Token);
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            UpdateTestCounts();
            var ran = cases.Count(c => _testCases[c.Id].State is TestState.Passed or TestState.Failed or TestState.Skipped);
            TestSummary = cts.IsCancellationRequested
                ? $"Test run canceled: {ran} of {cases.Count} test(s) ran."
                : $"Test run finished: {ran} test(s) ({PassedCount} Passed, {FailedCount} Failed, {SkippedCount} Skipped) run in {sw.Elapsed.TotalSeconds:0.0} sec";
            SetStatus(FailedCount > 0 ? $"{FailedCount} test(s) failed" : "Tests passed", StatusKind.Ready);
        }
        catch (Exception ex)
        {
            TestSummary = "Test run failed: " + ex.Message;
            SetStatus("Ready", StatusKind.Ready);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Builds the solution and discovers tests in every test project, keeping earlier results by test id.</summary>
    async Task<bool> DiscoverTestsAsync()
    {
        if (Solution is not { } solution)
            return false;
        if (!await RunBuildAsync("build", "Build"))
        {
            TestSummary = "Build failed; fix the errors to discover tests.";
            return false;
        }
        TestSummary = "Discovering tests...";
        var assemblies = new List<string>();
        foreach (var project in solution.Projects.Where(p => p.IsTestProject))
            if (await GetTargetPathAsync(project) is { } dll && File.Exists(dll))
                assemblies.Add(dll);
        if (assemblies.Count == 0)
        {
            TestSummary = "No test assemblies were built.";
            return false;
        }

        if (_testHost is null)
        {
            var console = await VsTestHost.FindConsoleAsync(solution.Directory);
            if (console is null)
            {
                TestSummary = "vstest.console was not found in the .NET SDK.";
                return false;
            }
            _testHost = VsTestHost.Start(console, Path.Combine(Path.GetTempPath(), "VSL", "vstest-logs"));
            _testHost.Log += BuildOutput.Append;
        }

        var cases = await _testHost.DiscoverAsync(assemblies, CancellationToken.None);
        _discovered.Clear();
        foreach (var c in cases)
            _discovered[c.Id] = c;
        BuildTestTree(cases);
        UpdateTestCounts();
        TestSummary = $"{cases.Count} test(s) discovered.";
        return true;
    }

    void BuildTestTree(IReadOnlyList<TestCase> cases)
    {
        // Same tests as before: keep the tree, so expansion, selection and results survive a run.
        if (_testCases.Count == cases.Count && cases.All(c => _testCases.ContainsKey(c.Id)))
        {
            foreach (var c in cases)
            {
                _testCases[c.Id].SourcePath = c.CodeFilePath;
                _testCases[c.Id].Line = c.LineNumber;
            }
            return;
        }
        var previous = _testCases.ToDictionary(kv => kv.Key, kv => kv.Value);
        _testCases.Clear();
        TestRoots.Clear();
        foreach (var byProject in cases.GroupBy(c => Path.GetFileNameWithoutExtension(c.Source)).OrderBy(g => g.Key))
        {
            var projectNode = new TestNode(TestNodeKind.Project, byProject.Key, null) { IsExpanded = true };
            foreach (var byClass in byProject.GroupBy(c => Before(c.FullyQualifiedName)).OrderBy(g => g.Key))
            {
                var ns = Before(byClass.Key);
                var nsNode = projectNode.Child(TestNodeKind.Namespace, ns.Length == 0 ? "<global>" : ns);
                var classNode = nsNode.Child(TestNodeKind.Class, After(byClass.Key));
                foreach (var byMethod in byClass.GroupBy(c => c.FullyQualifiedName).OrderBy(g => g.Key))
                {
                    var list = byMethod.ToList();
                    var methodName = After(byMethod.Key);
                    if (list.Count == 1)
                    {
                        Register(classNode.Child(TestNodeKind.Test, methodName), list[0]);
                        continue;
                    }
                    // Theory / parameterized test: one child per data row, named like VS does.
                    var methodNode = classNode.Child(TestNodeKind.Method, methodName);
                    methodNode.SourcePath = list[0].CodeFilePath;
                    methodNode.Line = list[0].LineNumber;
                    foreach (var c in list.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
                    {
                        var display = c.DisplayName.StartsWith(byMethod.Key, StringComparison.Ordinal) ? methodName + c.DisplayName[byMethod.Key.Length..] : c.DisplayName;
                        Register(methodNode.Child(TestNodeKind.Test, display), c);
                    }
                }
            }
            TestRoots.Add(projectNode);
        }

        void Register(TestNode node, TestCase c)
        {
            node.CaseId = c.Id;
            node.SourcePath = c.CodeFilePath;
            node.Line = c.LineNumber;
            _testCases[c.Id] = node;
            if (previous.TryGetValue(c.Id, out var old))
                node.SetResult(old.State, old.Duration, old.ErrorMessage, old.StackTrace, old.Output);
        }

        static string Before(string name) => name.LastIndexOf('.') is var i and >= 0 ? name[..i] : "";
        static string After(string name) => name.LastIndexOf('.') is var i and >= 0 ? name[(i + 1)..] : name;
    }

    void UpdateTestCounts()
    {
        PassedCount = _testCases.Values.Count(n => n.State == TestState.Passed);
        FailedCount = _testCases.Values.Count(n => n.State == TestState.Failed);
        SkippedCount = _testCases.Values.Count(n => n.State == TestState.Skipped);
    }

    public void NavigateToTest(TestNode? node)
    {
        if (node?.SourcePath is { } path && File.Exists(path))
        {
            var pos = new LspPosition(Math.Max(0, node.Line - 1), 0);
            NavigateTo(path, new LspRange(pos, pos));
        }
    }

    void ResetTesting()
    {
        _testCts?.Cancel();
        _testHost?.Dispose();
        _testHost = null;
        _testCases.Clear();
        _discovered.Clear();
        _lastRun = [];
        TestRoots.Clear();
        SelectedTest = null;
        PassedCount = FailedCount = SkippedCount = 0;
        TestSummary = "Build the solution to discover tests.";
    }
}

public enum TestNodeKind
{
    Project,
    Namespace,
    Class,
    Method,
    Test,
}

/// <summary>A Test Explorer row; group rows aggregate their children's outcome like VS.</summary>
public partial class TestNode(TestNodeKind kind, string name, TestNode? parent) : ObservableObject
{
    public TestNodeKind Kind { get; } = kind;
    public string Name { get; } = name;
    public TestNode? Parent { get; } = parent;
    public ObservableCollection<TestNode> Children { get; } = [];
    public Guid? CaseId { get; set; }
    public string? SourcePath { get; set; }
    public int Line { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateIcon))]
    public partial TestState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? StackTrace { get; set; }

    [ObservableProperty]
    public partial string? Output { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary><see cref="Views.VsIcon"/> kind for the outcome.</summary>
    public string StateIcon => State switch
    {
        TestState.Passed => "Ok",
        TestState.Failed => "Error",
        TestState.Skipped => "Warning",
        TestState.Running => "Running",
        _ => "Pending",
    };

    public string DurationText => Duration <= TimeSpan.Zero ? ""
        : Duration.TotalSeconds < 1 ? $"{Math.Max(1, (int)Duration.TotalMilliseconds)} ms"
        : $"{Duration.TotalSeconds:0.0} sec";

    public string Location => SourcePath is null ? "" : $"{Path.GetFileName(SourcePath)} line {Line}";

    public TestNode Child(TestNodeKind kind, string name)
    {
        var existing = Children.FirstOrDefault(c => c.Name == name && c.Kind == kind);
        if (existing is not null)
            return existing;
        var node = new TestNode(kind, name, this) { IsExpanded = kind != TestNodeKind.Method };
        Children.Add(node);
        return node;
    }

    public IEnumerable<Guid> CaseIds() =>
        CaseId is { } id ? [id] : Children.SelectMany(c => c.CaseIds());

    public void SetResult(TestState state, TimeSpan duration, string? error, string? stack, string? output)
    {
        State = state;
        Duration = duration;
        ErrorMessage = error;
        StackTrace = stack;
        Output = output;
        Parent?.Recompute();
    }

    void Recompute()
    {
        var states = Children.Select(c => c.State).ToList();
        State = states.Contains(TestState.Failed) ? TestState.Failed
            : states.Contains(TestState.Running) ? TestState.Running
            : states.Count > 0 && states.All(s => s == TestState.Skipped) ? TestState.Skipped
            : states.Count > 0 && states.All(s => s is TestState.Passed or TestState.Skipped) ? TestState.Passed
            : TestState.NotRun;
        Duration = Children.Aggregate(TimeSpan.Zero, (t, c) => t + c.Duration);
        Parent?.Recompute();
    }
}
