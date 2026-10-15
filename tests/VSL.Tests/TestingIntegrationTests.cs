using System.Collections.Concurrent;
using VSL.Build;
using VSL.Debugging;
using VSL.Testing;

namespace VSL.Tests;

/// <summary>Discovers and runs the sample's xUnit tests through the SDK's vstest.console.</summary>
[Collection(SampleSolutionCollection.Name)]
public class TestingIntegrationTests
{
    [Fact]
    public async Task Discovers_and_runs_xunit_tests_with_results_and_sources()
    {
        var ct = TestContext.Current.CancellationToken;
        var sln = await SampleSolution.GetAsync();
        var project = SampleSolution.PathOf(sln, "Tests/Tests.csproj");
        var build = new List<string>();
        using (var runner = ProcessRunner.Start(DotnetCli.Executable, ["build", project, "-nologo", "-v:quiet"], Path.GetDirectoryName(project)!, build.Add))
            Assert.True(await runner.WaitAsync(ct) == 0, string.Join("\n", build));

        var console = await VsTestHost.FindConsoleAsync(Path.GetDirectoryName(sln)!);
        Assert.NotNull(console);
        using var host = VsTestHost.Start(console, Path.Combine(Path.GetTempPath(), "vsl-test-logs"));
        var dll = SampleSolution.PathOf(sln, "Tests/bin/Debug/net10.0/Tests.dll");

        var tests = await host.DiscoverAsync([dll], ct);
        Assert.Equal(5, tests.Count);
        var failing = tests.Single(t => t.FullyQualifiedName == "Tests.GreetingTests.Deliberately_failing");
        Assert.EndsWith("GreetingTests.cs", failing.CodeFilePath);
        Assert.Equal(17, failing.LineNumber);

        var results = new ConcurrentDictionary<Guid, TestResultInfo>();
        await host.RunAsync(tests, r => results[r.Id] = r.State == TestState.Running && results.ContainsKey(r.Id) ? results[r.Id] : r, ct);
        Assert.Equal(TestState.Failed, results[failing.Id].State);
        Assert.Contains("Assert.Equal() Failure", results[failing.Id].ErrorMessage);
        Assert.Contains("GreetingTests.cs", results[failing.Id].StackTrace);
        Assert.Equal(3, results.Values.Count(r => r.State == TestState.Passed));
        Assert.Equal(1, results.Values.Count(r => r.State == TestState.Skipped));
    }

    [Fact]
    public async Task Debugs_a_test_and_stops_at_its_breakpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var sln = await SampleSolution.GetAsync();
        var project = SampleSolution.PathOf(sln, "Tests/Tests.csproj");
        using (var runner = ProcessRunner.Start(DotnetCli.Executable, ["build", project, "-nologo", "-v:quiet"], Path.GetDirectoryName(project)!, _ => { }))
            Assert.Equal(0, await runner.WaitAsync(ct));
        var console = await VsTestHost.FindConsoleAsync(Path.GetDirectoryName(sln)!);
        using var host = VsTestHost.Start(console!, Path.Combine(Path.GetTempPath(), "vsl-test-logs"));
        var tests = await host.DiscoverAsync([SampleSolution.PathOf(sln, "Tests/bin/Debug/net10.0/Tests.dll")], ct);
        var target = tests.Single(t => t.FullyQualifiedName == "Tests.GreetingTests.Default_name_is_world");

        var adapter = await NetcoredbgLocator.EnsureInstalledAsync(_ => { }, ct);
        var source = SampleSolution.PathOf(sln, "Tests/GreetingTests.cs");
        var stops = new BlockingCollection<(int Thread, string Reason)>();
        var log = new ConcurrentQueue<string>();
        DebugSession? session = null;
        var launcher = new DebuggingTestHostLauncher(async pid =>
        {
            log.Enqueue($"[launch] testhost pid {pid}");
            session = await DebugSession.AttachAsync(adapter, pid, new Dictionary<string, IReadOnlyList<int>> { [source] = [9] },
                (file, r) => log.Enqueue($"[bp] {Path.GetFileName(file)} verified={string.Join(",", r.Select(x => x.Verified))}"),
                s =>
                {
                    s.Stopped += (thread, reason, text) => { log.Enqueue($"[stopped] {reason} {text}"); stops.Add((thread, reason)); };
                    s.Output += (cat, text) => log.Enqueue($"[{cat}] {text.TrimEnd()}");
                }, ct);
            log.Enqueue("[attach] done");
        }, line => log.Enqueue("[testhost] " + line));

        var results = new ConcurrentDictionary<Guid, TestResultInfo>();
        var run = host.DebugAsync([target], launcher, r => { if (r.State != TestState.Running) results[r.Id] = r; }, ct);

        Assert.True(stops.TryTake(out var stop, TimeSpan.FromSeconds(45)), "breakpoint in the test was not hit:\n" + string.Join("\n", log));
        Assert.Equal("breakpoint", stop.Reason);
        var frame = (await session!.StackTraceAsync(stop.Thread, ct))[0];
        Assert.EndsWith("GreetingTests.cs", frame.SourcePath);
        Assert.Equal(9, frame.Line);
        await session.ContinueAsync(stop.Thread);

        await run.WaitAsync(TimeSpan.FromSeconds(90), ct);
        Assert.Equal(TestState.Passed, results[target.Id].State);
        await session.DisposeAsync();
    }
}
