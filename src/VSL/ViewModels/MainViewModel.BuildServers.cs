using VSL.Build;

namespace VSL.ViewModels;

/// <summary>
/// `dotnet build` leaves the C# compiler server (VBCSCompiler) and MSBuild nodes running for the next build, about a
/// gigabyte together, and they only exit after several idle minutes. VSL keeps them while it is open and shuts them
/// down when it exits, if it built anything.
/// </summary>
public partial class MainViewModel
{
    bool _usedBuildServers;

    /// <summary>`dotnet build-server shutdown` (replaced in tests, whose builds share the servers with other tests).</summary>
    public Func<Task> ShutdownBuildServers { get; set; } = ShutdownBuildServersCoreAsync;

    public async Task ShutdownBuildServersAsync()
    {
        if (!_usedBuildServers)
            return;
        _usedBuildServers = false;
        await ShutdownBuildServers();
    }

    static async Task ShutdownBuildServersCoreAsync()
    {
        try
        {
            using var runner = ProcessRunner.Start(DotnetCli.Executable, ["build-server", "shutdown"],
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _ => { });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await runner.WaitAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Leaving them to exit on their own is fine.
        }
    }
}
