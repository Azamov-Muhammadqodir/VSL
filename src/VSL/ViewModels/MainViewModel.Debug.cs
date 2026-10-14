using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Build;
using VSL.Debugging;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>Debugger integration: F5 / F10 / F11, breakpoints, Locals, Watch and Call Stack.</summary>
public partial class MainViewModel
{
    public const int LocalsTab = 3;
    public const int WatchTab = 4;
    public const int CallStackTab = 5;

    readonly Dictionary<(string Project, string Configuration), string> _targetPaths = [];
    DebugSession? _session;
    int _stoppedThread;
    string? _debuggedProject;

    public BreakpointStore Breakpoints { get; } = new();
    public ObservableCollection<StackFrameItem> CallStack { get; } = [];
    public ObservableCollection<VariableNode> Locals { get; } = [];
    public ObservableCollection<WatchItem> Watches { get; } = [];

    /// <summary>Raised when the debugger breaks, so the window can come to the front like VS does.</summary>
    public event Action? BreakModeEntered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugging), nameof(IsInBreakMode), nameof(StartButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartDebuggingCommand), nameof(StartWithoutDebuggingCommand), nameof(StopCommand),
        nameof(RestartCommand), nameof(StepOverCommand), nameof(StepIntoCommand), nameof(StepOutCommand), nameof(BreakAllCommand))]
    public partial DebugMode Mode { get; set; }

    [ObservableProperty]
    public partial StackFrameItem? SelectedFrame { get; set; }

    public bool IsDebugging => Mode != DebugMode.Design;

    public bool IsInBreakMode => Mode == DebugMode.Break;

    public string StartButtonText => Mode == DebugMode.Design ? LaunchProfile ?? "Start" : "Continue";

    void InitializeDebugging()
    {
        Breakpoints.Changed += (file, fromEditor) => _ = OnBreakpointsChangedAsync(file);
    }

    async Task OnBreakpointsChangedAsync(string file)
    {
        if (Solution is not null)
            Settings.For(Solution.FilePath).Breakpoints = Breakpoints.Snapshot();
        if (_session is not { HasEnded: false } session)
            return;
        // Breakpoints added or removed while debugging take effect immediately.
        var lines = Breakpoints.For(file).ToList();
        try
        {
            var results = await session.SetBreakpointsAsync(file, lines);
            Breakpoints.SetVerification(file, lines, results);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append("Failed to update breakpoints: " + ex.Message);
        }
    }

    void LoadDebugState(Services.Settings.SolutionState state)
    {
        Breakpoints.Load(state.Breakpoints);
        Watches.Clear();
        foreach (var w in state.Watches)
            Watches.Add(new WatchItem(w));
    }

    void SaveDebugState(Services.Settings.SolutionState state)
    {
        state.Breakpoints = Breakpoints.Snapshot();
        state.Watches = Watches.Select(w => w.Expression).ToList();
    }

    // ---- Start / continue / stop -----------------------------------------------------------------------------

    bool CanStartDebugging => Mode == DebugMode.Break
        || (Mode == DebugMode.Design && Solution is not null && StartupProject is not null && !IsBuilding && !IsRunning);

    [RelayCommand(CanExecute = nameof(CanStartDebugging))]
    async Task StartDebugging()
    {
        if (Mode == DebugMode.Break && _session is { } running)
        {
            BeginRunning();
            await running.ContinueAsync(_stoppedThread);
            return;
        }
        await LaunchDebuggerAsync();
    }

    async Task LaunchDebuggerAsync()
    {
        if (Solution is null || StartupProject is not { } project)
            return;
        if (!await RunBuildAsync("build", "Build", project.FilePath))
            return;

        DebugOutput.Clear();
        SelectedOutput = DebugOutput;
        BottomTabIndex = 1;
        var program = await GetTargetPathAsync(project);
        if (program is null || !File.Exists(program))
        {
            DebugOutput.Append($"Could not find the output assembly of {project.Name}.");
            return;
        }

        string adapter;
        try
        {
            adapter = await NetcoredbgLocator.EnsureInstalledAsync(DebugOutput.Append, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DebugOutput.Append("Could not install the debugger (netcoredbg): " + ex.Message);
            return;
        }

        var profile = project.LaunchProfiles.FirstOrDefault(p => p.Name == LaunchProfile);
        var env = new Dictionary<string, string>(profile?.EnvironmentVariables ?? new Dictionary<string, string>());
        // dotnet run turns applicationUrl into ASPNETCORE_URLS; launching the dll directly must do the same.
        if (profile?.ApplicationUrl is { Length: > 0 } urls && !env.ContainsKey("ASPNETCORE_URLS"))
            env["ASPNETCORE_URLS"] = urls;
        var args = new List<string> { program };
        if (profile?.CommandLineArgs is { Length: > 0 } extra)
            args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        DebugOutput.Append($"Debugging {project.Name} ({Path.GetFileName(program)}) with netcoredbg {NetcoredbgLocator.Version}");
        _debuggedProject = project.Name;
        Mode = DebugMode.Running;
        SetStatus("Running", StatusKind.Running);
        var browserOpened = false;
        var breakpoints = Breakpoints.All;
        try
        {
            _session = await DebugSession.LaunchAsync(
                adapter,
                new LaunchRequest(DotnetCli.Executable, args, project.Directory, env),
                breakpoints,
                (file, results) => Dispatcher.UIThread.Post(() => Breakpoints.SetVerification(file, breakpoints[file], results)),
                session =>
                {
                    session.Stopped += (thread, reason, text) => Dispatcher.UIThread.Post(() => _ = OnStoppedAsync(thread, reason, text));
                    session.Continued += () => Dispatcher.UIThread.Post(BeginRunning);
                    session.Ended += code => Dispatcher.UIThread.Post(() => OnDebugEnded(session, code));
                    session.BreakpointChanged += (file, line, verified) => Dispatcher.UIThread.Post(() =>
                        Breakpoints.SetVerification(file, [line], [new BreakpointResult(verified, line, null)]));
                    session.Output += (category, text) =>
                    {
                        DebugOutput.AppendRaw(text);
                        if (!browserOpened && profile is { LaunchBrowser: true } && text.Contains("Now listening on:", StringComparison.Ordinal))
                        {
                            browserOpened = true;
                            var url = Regex.Match(text, @"https?://\S+").Value;
                            if (url.Length > 0)
                                OpenBrowser(url.TrimEnd('/') + "/" + (profile.LaunchUrl ?? ""));
                        }
                    };
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            DebugOutput.Append("Failed to start debugging: " + ex.Message);
            _session = null;
            Mode = DebugMode.Design;
            SetStatus("Ready", StatusKind.Ready);
        }
    }

    /// <summary>The project's output assembly, as MSBuild computes it (honors AssemblyName, OutputPath, TFM).</summary>
    async Task<string?> GetTargetPathAsync(ProjectInfo project)
    {
        if (_targetPaths.TryGetValue((project.FilePath, Configuration), out var cached))
            return cached;
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable,
            ["msbuild", project.FilePath, "-getProperty:TargetPath", $"-p:Configuration={Configuration}", "-nologo"],
            project.Directory, lines.Add);
        if (await runner.WaitAsync() != 0)
            return null;
        var path = lines.Select(l => l.Trim()).LastOrDefault(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (path is not null)
            _targetPaths[(project.FilePath, Configuration)] = path;
        return path;
    }

    bool CanStepping => Mode == DebugMode.Break && _session is not null;

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepOver() => StepAsync(s => s.StepOverAsync(_stoppedThread));

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepInto() => StepAsync(s => s.StepInAsync(_stoppedThread));

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepOut() => StepAsync(s => s.StepOutAsync(_stoppedThread));

    async Task StepAsync(Func<DebugSession, Task> step)
    {
        if (_session is not { } session)
            return;
        BeginRunning();
        try
        {
            await step(session);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunningUnderDebugger))]
    async Task BreakAll()
    {
        if (_session is { } session)
            await session.PauseAsync();
    }

    bool IsRunningUnderDebugger => Mode == DebugMode.Running;

    bool CanStop => IsRunning || Mode != DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanStop))]
    async Task Stop()
    {
        _runProcess?.Kill();
        if (_session is { } session)
            await session.TerminateAsync();
    }

    [RelayCommand(CanExecute = nameof(IsDebugging))]
    async Task Restart()
    {
        if (_session is { } session)
        {
            var ended = new TaskCompletionSource();
            session.Ended += _ => ended.TrySetResult();
            await session.TerminateAsync();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        await LaunchDebuggerAsync();
    }

    [RelayCommand]
    void DeleteAllBreakpoints()
    {
        foreach (var file in Breakpoints.All.Keys.ToList())
            Breakpoints.Replace(file, []);
        foreach (var doc in Documents)
            doc.Host.AttachBreakpoints(Breakpoints);
    }

    // ---- Break mode ------------------------------------------------------------------------------------------

    void BeginRunning()
    {
        if (Mode == DebugMode.Design)
            return;
        Mode = DebugMode.Running;
        SetStatus("Running", StatusKind.Running);
        SelectedFrame = null;
        CallStack.Clear();
        Locals.Clear();
        foreach (var doc in Documents)
        {
            doc.Host.SetExecutionLine(null);
            doc.Host.DataTipProvider = null;
        }
    }

    async Task OnStoppedAsync(int threadId, string reason, string? text)
    {
        if (_session is not { } session)
            return;
        _stoppedThread = threadId;
        Mode = DebugMode.Break;
        SetStatus(reason == "exception" ? $"Exception thrown: {text}" : "Break mode", StatusKind.Running);
        if (reason == "exception")
            DebugOutput.Append($"Exception thrown: {text}");

        IReadOnlyList<StackFrameInfo> frames;
        try
        {
            frames = await session.StackTraceAsync(threadId);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append("Could not read the call stack: " + ex.Message);
            return;
        }
        CallStack.Clear();
        foreach (var f in frames)
            CallStack.Add(new StackFrameItem(f.Id, f.Name, f.SourcePath, f.Line, f.Column));
        foreach (var doc in Documents)
            doc.Host.DataTipProvider = EvaluateDataTipAsync;
        SelectedFrame = CallStack.FirstOrDefault(f => f.HasSource) ?? CallStack.FirstOrDefault();
        if (BottomTabIndex < LocalsTab)
            BottomTabIndex = LocalsTab;
        BreakModeEntered?.Invoke();
    }

    partial void OnSelectedFrameChanged(StackFrameItem? value)
    {
        if (value is null || Mode != DebugMode.Break)
            return;
        foreach (var doc in Documents)
            doc.Host.SetExecutionLine(null);
        if (value.HasSource && OpenDocument(value.SourcePath!) is { } doc2)
            Dispatcher.UIThread.Post(() => doc2.Host.SetExecutionLine(value.Line), DispatcherPriority.Loaded);
        _ = LoadLocalsAsync(value.Id);
        _ = RefreshWatchesAsync();
    }

    async Task LoadLocalsAsync(int frameId)
    {
        Locals.Clear();
        if (_session is not { } session)
            return;
        try
        {
            var scopes = await session.ScopesAsync(frameId);
            if (scopes.Count == 1)
            {
                foreach (var v in await session.VariablesAsync(scopes[0].VariablesReference))
                    Locals.Add(Node(session, v));
            }
            else
            {
                foreach (var scope in scopes)
                    Locals.Add(new VariableNode(scope.Name, "", null, () => Children(session, scope.VariablesReference)));
            }
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Locals.Add(new VariableNode("", ex.Message, null, null));
        }
    }

    static VariableNode Node(DebugSession session, VariableInfo v) =>
        new(v.Name, v.Value, v.Type, v.VariablesReference > 0 && v.Value != "null" ? () => Children(session, v.VariablesReference) : null);

    static async Task<IReadOnlyList<VariableNode>> Children(DebugSession session, int reference) =>
        (await session.VariablesAsync(reference)).Select(v => Node(session, v)).ToList();

    async Task<string?> EvaluateDataTipAsync(string expression)
    {
        if (_session is not { } session || Mode != DebugMode.Break)
            return null;
        try
        {
            return (await session.EvaluateAsync(expression, SelectedFrame?.Id, "hover")).Value;
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            return null;
        }
    }

    [RelayCommand]
    async Task AddWatch(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return;
        var item = new WatchItem(expression.Trim());
        Watches.Add(item);
        if (Solution is not null)
            SaveDebugState(Settings.For(Solution.FilePath));
        await EvaluateWatchAsync(item);
    }

    [RelayCommand]
    void RemoveWatch(WatchItem? item)
    {
        if (item is null)
            return;
        Watches.Remove(item);
        if (Solution is not null)
            SaveDebugState(Settings.For(Solution.FilePath));
    }

    async Task RefreshWatchesAsync()
    {
        foreach (var w in Watches.ToList())
            await EvaluateWatchAsync(w);
    }

    async Task EvaluateWatchAsync(WatchItem item)
    {
        if (_session is not { } session || Mode != DebugMode.Break)
        {
            item.Value = "";
            item.Type = "";
            return;
        }
        try
        {
            var result = await session.EvaluateAsync(item.Expression, SelectedFrame?.Id, "watch");
            item.Value = result.Value;
            item.Type = result.Type ?? "";
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            item.Value = ex is DapException dap ? dap.Message[(dap.Command.Length + 2)..] : ex.Message;
            item.Type = "";
        }
    }

    void OnDebugEnded(DebugSession session, int? exitCode)
    {
        if (_session != session)
            return;
        _session = null;
        foreach (var doc in Documents)
        {
            doc.Host.SetExecutionLine(null);
            doc.Host.DataTipProvider = null;
        }
        CallStack.Clear();
        Locals.Clear();
        foreach (var w in Watches)
        {
            w.Value = "";
            w.Type = "";
        }
        Breakpoints.ClearVerification();
        DebugOutput.Append($"The program '{_debuggedProject}' has exited with code {exitCode ?? 0} (0x{exitCode ?? 0:x}).");
        Mode = DebugMode.Design;
        SetStatus("Ready", Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
        _ = session.DisposeAsync().AsTask();
    }
}
