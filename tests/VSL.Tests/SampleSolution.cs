using System.Diagnostics;
using System.Text;
using VSL.Build;

namespace VSL.Tests;

/// <summary>
/// Tests that build or run the shared sample solution must not run in parallel: concurrent MSBuild runs over the
/// same obj/ folders fail with "file is being used by another process".
/// </summary>
[CollectionDefinition(Name)]
public sealed class SampleSolutionCollection
{
    public const string Name = "Sample solution";
}

/// <summary>
/// A small Blazor solution (web host + Razor component library) copied to a temp directory and restored once per run.
/// Copying keeps MSBuild from picking up VSL's own Directory.Build.props.
/// </summary>
public static class SampleSolution
{
    static readonly Lazy<Task<string>> _prepared = new(PrepareAsync);

    static SampleSolution()
    {
        // Never touch the user's real ~/.config/VSL from tests.
        Environment.SetEnvironmentVariable("VSL_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "vsl-test-config"));
    }

    public static void EnsureConfigIsolated() => _ = ArtifactsDir;

    /// <summary>Returns the path of the restored BlazorSample.sln.</summary>
    public static Task<string> GetAsync() => _prepared.Value;

    public static string ArtifactsDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("VSL_TEST_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "vsl-test-artifacts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static async Task<string> PrepareAsync()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample");
        var target = Path.Combine(Path.GetTempPath(), "vsl-sample-" + Environment.ProcessId);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }

        var sln = Path.Combine(target, "BlazorSample.sln");
        await RunDotnetAsync("Restoring the sample solution", "restore", sln);
        return sln;
    }

    /// <summary>
    /// Runs a dotnet command to the end, failing with its output if it fails or takes over 5 minutes. Without build
    /// servers: an MSBuild node left running keeps the output pipes open, and on a fresh CI machine waiting for them to
    /// close waited for the node's idle timeout, longer than the whole test run may take.
    /// </summary>
    internal static async Task RunDotnetAsync(string what, params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetCli.Executable, [.. args, "--disable-build-servers"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        DotnetCli.ApplyEnvironment(psi);
        var output = new StringBuilder();
        void Collect(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
                lock (output)
                    output.AppendLine(e.Data);
        }
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // What was still running tells where it stuck (a restore, an MSBuild node, a NuGet credential provider...).
            var processes = DotnetProcesses();
            process.Kill(entireProcessTree: true);
            lock (output)
                throw new TimeoutException($"{what} took over 5 minutes:\n{output}\nRunning dotnet processes:\n{processes}");
        }
        if (process.ExitCode != 0)
            lock (output)
                throw new InvalidOperationException($"{what} failed:\n{output}");
    }

    static string DotnetProcesses()
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", ["-eo", "pid,ppid,etime,args"]) { RedirectStandardOutput = true })!;
            var lines = ps.StandardOutput.ReadToEnd().Split('\n');
            ps.WaitForExit();
            return string.Join('\n', lines.Take(1).Concat(lines.Where(l => l.Contains("dotnet", StringComparison.Ordinal) || l.Contains("MSBuild", StringComparison.Ordinal))));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "(ps unavailable)";
        }
    }

    public static string PathOf(string sln, string relative) => Path.Combine(Path.GetDirectoryName(sln)!, relative);
}
