using System.Diagnostics;

namespace VSL.Build;

/// <summary>Runs a child process, streams its output line by line and kills the whole tree on cancel.</summary>
public sealed class ProcessRunner : IDisposable
{
    readonly Process _process;

    ProcessRunner(Process process) => _process = process;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public static ProcessRunner Start(string fileName, IEnumerable<string> args, string workingDirectory,
        Action<string> onOutput, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        DotnetCli.ApplyEnvironment(psi);
        // Keep MSBuild output machine-readable and colorless.
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        psi.Environment["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "0";
        if (environment is not null)
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ProcessRunner(process);
    }

    public async Task<int> WaitAsync(CancellationToken ct = default)
    {
        try
        {
            await _process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill();
            await _process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        // WaitForExitAsync returns before the async output readers drain; WaitForExit() flushes them.
        _process.WaitForExit();
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose() => _process.Dispose();
}
