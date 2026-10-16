using System.Diagnostics;

namespace VSL.Build;

/// <summary>Runs a child process, streams its output line by line and kills the whole tree on cancel.</summary>
public sealed class ProcessRunner : IDisposable
{
    /// <summary>How long the output still in the pipes may take once the process has exited.</summary>
    static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    readonly Process _process;
    readonly Task _exited;

    ProcessRunner(Process process, Task exited)
    {
        _process = process;
        _exited = exited;
    }

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
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ProcessRunner(process, exited.Task);
    }

    /// <summary>
    /// Waits for the process to exit and for its remaining output. Not for the pipes to close: WaitForExitAsync waits
    /// for that, and a process the command started and left running (an MSBuild node, the compiler server, a program
    /// that forks) keeps them open until it exits itself, which made a finished build look like it ran for minutes.
    /// </summary>
    public async Task<int> WaitAsync(CancellationToken ct = default)
    {
        try
        {
            await _exited.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill();
            await _exited;
            throw;
        }
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(OutputDrainTimeout);
        }
        catch (TimeoutException)
        {
            // Output after this point comes from the processes left running, not from this one.
        }
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
