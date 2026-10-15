using System.Diagnostics;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
using VSL.Build;

namespace VSL.Testing;

/// <summary>
/// Debugger hook for "Debug Tests". In design mode vstest starts testhost itself and asks the IDE to attach
/// (ITestHostLauncher2/3); otherwise it asks us to launch the host, which we start waiting for a debugger
/// (VSTEST_HOST_DEBUG). Either way netcoredbg attaches and sets breakpoints before any test runs.
/// </summary>
public sealed class DebuggingTestHostLauncher(Func<int, Task> attachDebugger, Action<string> output) : ITestHostLauncher3
{
    public bool AttachDebuggerToProcess(int pid) => AttachDebuggerToProcess(pid, CancellationToken.None);

    public bool AttachDebuggerToProcess(int pid, CancellationToken cancellationToken)
    {
        try
        {
            attachDebugger(pid).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            output("Could not attach the debugger to the test host: " + ex.Message);
            return false;
        }
    }

    public bool AttachDebuggerToProcess(AttachDebuggerInfo attachDebuggerInfo, CancellationToken cancellationToken) =>
        AttachDebuggerToProcess(attachDebuggerInfo.ProcessId, cancellationToken);

    public bool IsDebug => true;

    public int LaunchTestHost(TestProcessStartInfo defaultTestHostStartInfo) =>
        LaunchTestHost(defaultTestHostStartInfo, CancellationToken.None);

    public int LaunchTestHost(TestProcessStartInfo info, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(info.FileName!, info.Arguments ?? "")
        {
            WorkingDirectory = info.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        DotnetCli.ApplyEnvironment(psi);
        foreach (var (key, value) in info.EnvironmentVariables ?? new Dictionary<string, string?>())
            psi.Environment[key] = value;
        psi.Environment["VSTEST_HOST_DEBUG"] = "1";
        // Skip the Debugger.Break() testhost does after the debugger attaches.
        psi.Environment["VSTEST_DEBUG_NOBP"] = "1";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the test host.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        attachDebugger(process.Id).GetAwaiter().GetResult();
        return process.Id;
    }
}
