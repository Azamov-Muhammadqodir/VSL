using System.Collections.Concurrent;
using System.Diagnostics;
using VSL.Build;

namespace VSL.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Returns_when_the_process_exits_although_a_child_keeps_its_output_open()
    {
        // Like a build that leaves an MSBuild node or the compiler server running with the build's output pipes.
        var lines = new ConcurrentQueue<string>();
        using var runner = ProcessRunner.Start("bash", ["-c", "echo first; sleep 20 & echo last"], Path.GetTempPath(), lines.Enqueue);
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(0, await runner.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"waited {elapsed.Elapsed}");
        Assert.Equal(["first", "last"], lines);
    }

    [Fact]
    public async Task Cancelling_kills_the_process()
    {
        using var runner = ProcessRunner.Start("bash", ["-c", "sleep 30"], Path.GetTempPath(), _ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.WaitAsync(cts.Token));
        Assert.True(runner.HasExited);
    }
}
