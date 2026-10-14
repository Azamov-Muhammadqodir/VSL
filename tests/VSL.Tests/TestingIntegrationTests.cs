using System.Collections.Concurrent;
using VSL.Build;
using VSL.Testing;

namespace VSL.Tests;

/// <summary>Discovers and runs the sample's xUnit tests through the SDK's vstest.console.</summary>
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
}
