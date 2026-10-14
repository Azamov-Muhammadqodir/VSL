using System.Collections.Concurrent;
using VSL.Build;
using VSL.Debugging;

namespace VSL.Tests;

/// <summary>Debugs the sample web host with the real netcoredbg.</summary>
public class DebuggerIntegrationTests
{
    [Fact]
    public async Task Breakpoint_inspect_step_and_continue()
    {
        var ct = TestContext.Current.CancellationToken;
        var sln = await SampleSolution.GetAsync();
        var project = SampleSolution.PathOf(sln, "Web/Web.csproj");
        var build = new List<string>();
        using (var runner = ProcessRunner.Start(DotnetCli.Executable, ["build", project, "-nologo", "-v:quiet"], Path.GetDirectoryName(project)!, build.Add))
            Assert.True(await runner.WaitAsync(ct) == 0, string.Join("\n", build));

        var adapter = await NetcoredbgLocator.EnsureInstalledAsync(_ => { }, ct);
        var program = SampleSolution.PathOf(sln, "Web/Program.cs");
        var dll = SampleSolution.PathOf(sln, "Web/bin/Debug/net10.0/Web.dll");
        var stops = new BlockingCollection<(int Thread, string Reason)>();
        var output = new ConcurrentQueue<string>();
        var ended = new TaskCompletionSource<int?>();
        var verified = new Dictionary<string, IReadOnlyList<BreakpointResult>>();

        await using var session = await DebugSession.LaunchAsync(
            adapter,
            new LaunchRequest(DotnetCli.Executable, [dll], Path.GetDirectoryName(project)!, new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = "http://localhost:5281",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }),
            new Dictionary<string, IReadOnlyList<int>> { [program] = [2] },
            (file, results) => verified[file] = results,
            s =>
            {
                s.Stopped += (thread, reason, _) => stops.Add((thread, reason));
                s.Output += (_, text) => output.Enqueue(text);
                s.Ended += code => ended.TrySetResult(code);
            },
            ct);

        Assert.Single(verified[program]);

        // Hits the breakpoint on "var app = builder.Build();".
        Assert.True(stops.TryTake(out var stop, TimeSpan.FromSeconds(60)), "breakpoint was not hit; output:\n" + string.Concat(output));
        Assert.Equal("breakpoint", stop.Reason);
        var frames = await session.StackTraceAsync(stop.Thread, ct);
        Assert.EndsWith("Program.cs", frames[0].SourcePath);
        Assert.Equal(2, frames[0].Line);

        var locals = (await session.ScopesAsync(frames[0].Id, ct))[0];
        var variables = await session.VariablesAsync(locals.VariablesReference, ct);
        Assert.Contains(variables, v => v.Name == "builder");
        var env = await session.EvaluateAsync("builder.Environment.EnvironmentName", frames[0].Id, "watch", ct);
        Assert.Equal("\"Development\"", env.Value);

        // F10 moves to the next statement.
        await session.StepOverAsync(stop.Thread);
        Assert.True(stops.TryTake(out var step, TimeSpan.FromSeconds(30)));
        Assert.Equal("step", step.Reason);
        Assert.Equal(4, (await session.StackTraceAsync(step.Thread, ct))[0].Line);

        // Continue: the host starts listening; Stop ends the session.
        await session.ContinueAsync(step.Thread);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!string.Concat(output).Contains("Now listening on: http://localhost:5281") && DateTime.UtcNow < deadline)
            await Task.Delay(200, ct);
        Assert.Contains("Now listening on: http://localhost:5281", string.Concat(output));
        await session.TerminateAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }
}
