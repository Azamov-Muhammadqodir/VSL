using System.Diagnostics;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using VSL.Build;

namespace VSL.Testing;

public enum TestState
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped,
}

public sealed record TestResultInfo(Guid Id, TestState State, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Output);

/// <summary>
/// Discovers and runs tests through the SDK's vstest.console, the same test platform Visual Studio uses,
/// so every adapter (xUnit, NUnit, MSTest) works and results carry source locations, durations and stack traces.
/// </summary>
public sealed class VsTestHost : IDisposable
{
    const string RunSettings = """
        <RunSettings>
          <RunConfiguration>
            <DesignMode>true</DesignMode>
            <CollectSourceInformation>true</CollectSourceInformation>
          </RunConfiguration>
        </RunSettings>
        """;

    readonly VsTestConsoleWrapper _wrapper;
    readonly SemaphoreSlim _gate = new(1, 1);

    VsTestHost(VsTestConsoleWrapper wrapper) => _wrapper = wrapper;

    public event Action<string>? Log;

    /// <summary>vstest.console.dll of the SDK that `dotnet` resolves in the solution directory (honours global.json).</summary>
    public static async Task<string?> FindConsoleAsync(string solutionDirectory)
    {
        var psi = new ProcessStartInfo(DotnetCli.Executable, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = solutionDirectory,
        };
        DotnetCli.ApplyEnvironment(psi);
        using var p = Process.Start(psi)!;
        var version = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        var path = Path.Combine(DotnetCli.Root, "sdk", version, "vstest.console.dll");
        return p.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    public static VsTestHost Start(string consolePath, string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        // vstest's diagnostic trace is large; only write it when asked to (VSL_VSTEST_LOG=1).
        var parameters = new ConsoleParameters();
        if (Environment.GetEnvironmentVariable("VSL_VSTEST_LOG") == "1")
            parameters.LogFilePath = Path.Combine(logDirectory, "vstest.log");
        parameters.EnvironmentVariables = new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = DotnetCli.Root,
            ["PATH"] = DotnetCli.Root + ":" + Environment.GetEnvironmentVariable("PATH"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
        var wrapper = new VsTestConsoleWrapper(consolePath, parameters);
        wrapper.StartSession();
        return new VsTestHost(wrapper);
    }

    public async Task<IReadOnlyList<TestCase>> DiscoverAsync(IReadOnlyList<string> assemblies, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var handler = new DiscoveryHandler(m => Log?.Invoke(m));
            await Task.Run(() => _wrapper.DiscoverTests(assemblies, RunSettings, new TestPlatformOptions { CollectMetrics = false }, handler), ct);
            return handler.Tests;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the given tests; results stream through <paramref name="onUpdate"/> as they finish.</summary>
    public async Task<TimeSpan> RunAsync(IReadOnlyList<TestCase> tests, Action<TestResultInfo> onUpdate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var handler = new RunHandler(onUpdate, m => Log?.Invoke(m));
            await using var reg = ct.Register(_wrapper.CancelTestRun);
            await Task.Run(() => _wrapper.RunTests(tests, RunSettings, new TestPlatformOptions { CollectMetrics = false }, handler), CancellationToken.None);
            return handler.Elapsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs tests with a host launcher that starts testhost under the debugger.</summary>
    public async Task<TimeSpan> DebugAsync(IReadOnlyList<TestCase> tests, Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces.ITestHostLauncher launcher,
        Action<TestResultInfo> onUpdate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var handler = new RunHandler(onUpdate, m => Log?.Invoke(m));
            await using var reg = ct.Register(_wrapper.CancelTestRun);
            await Task.Run(() => _wrapper.RunTestsWithCustomTestHost(tests, RunSettings, new TestPlatformOptions { CollectMetrics = false }, handler, launcher), CancellationToken.None);
            return handler.Elapsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _wrapper.EndSession();
        }
        catch (Exception)
        {
            // vstest.console already gone.
        }
    }

    sealed class DiscoveryHandler(Action<string> log) : ITestDiscoveryEventsHandler2
    {
        readonly List<TestCase> _tests = [];

        public IReadOnlyList<TestCase> Tests
        {
            get
            {
                lock (_tests)
                    return _tests.ToList();
            }
        }

        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
        {
            lock (_tests)
                _tests.AddRange(discoveredTestCases ?? []);
        }

        public void HandleDiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase>? lastChunk) =>
            HandleDiscoveredTests(lastChunk);

        public void HandleLogMessage(TestMessageLevel level, string? message)
        {
            if (level != TestMessageLevel.Informational && message is not null)
                log(message);
        }

        public void HandleRawMessage(string rawMessage)
        {
        }
    }

    sealed class RunHandler(Action<TestResultInfo> onUpdate, Action<string> log) : ITestRunEventsHandler
    {
        public TimeSpan Elapsed { get; private set; }

        public void HandleTestRunStatsChange(TestRunChangedEventArgs? args)
        {
            if (args is null)
                return;
            foreach (var running in args.ActiveTests ?? [])
                onUpdate(new TestResultInfo(running.Id, TestState.Running, TimeSpan.Zero, null, null, null));
            Report(args.NewTestResults);
        }

        public void HandleTestRunComplete(TestRunCompleteEventArgs testRunCompleteArgs, TestRunChangedEventArgs? lastChunkArgs,
            ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
        {
            Report(lastChunkArgs?.NewTestResults);
            Elapsed = testRunCompleteArgs.ElapsedTimeInRunningTests;
            if (testRunCompleteArgs.Error is { } error)
                log(error.Message);
        }

        void Report(IEnumerable<TestResult>? results)
        {
            foreach (var r in results ?? [])
            {
                var state = r.Outcome switch
                {
                    TestOutcome.Passed => TestState.Passed,
                    TestOutcome.Failed => TestState.Failed,
                    TestOutcome.Skipped => TestState.Skipped,
                    _ => TestState.NotRun,
                };
                var output = string.Join("\n", r.Messages.Select(m => m.Text?.TrimEnd()).Where(t => !string.IsNullOrEmpty(t)));
                onUpdate(new TestResultInfo(r.TestCase.Id, state, r.Duration, r.ErrorMessage, r.ErrorStackTrace, output.Length == 0 ? null : output));
            }
        }

        public void HandleLogMessage(TestMessageLevel level, string? message)
        {
            if (level != TestMessageLevel.Informational && message is not null)
                log(message);
        }

        public void HandleRawMessage(string rawMessage)
        {
        }

        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => -1;
    }
}
