using System.Diagnostics;
using VSL.Build;

namespace VSL.Tests;

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
        var psi = new ProcessStartInfo(DotnetCli.Executable, ["restore", sln]) { RedirectStandardOutput = true, RedirectStandardError = true };
        DotnetCli.ApplyEnvironment(psi);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("Restoring the sample solution failed:\n" + output);
        return sln;
    }

    public static string PathOf(string sln, string relative) => Path.Combine(Path.GetDirectoryName(sln)!, relative);
}
