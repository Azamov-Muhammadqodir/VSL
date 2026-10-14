using System.Diagnostics;

namespace VSL.Build;

/// <summary>Locates the dotnet host so child processes work even when VSL is launched from the desktop (no shell rc).</summary>
public static class DotnetCli
{
    static readonly Lazy<string> _executable = new(Locate);

    public static string Executable => _executable.Value;

    public static string Root => Path.GetDirectoryName(Executable)!;

    static string Locate()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "dotnet")))
            return Path.Combine(root, "dotnet");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userInstall = Path.Combine(home, ".dotnet", "dotnet");
        if (File.Exists(userInstall))
            return userInstall;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "dotnet");
            if (File.Exists(candidate))
                return new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }
        return "dotnet";
    }

    /// <summary>Applies DOTNET_ROOT/PATH so apphosts and the SDK resolve the same installation.</summary>
    public static void ApplyEnvironment(ProcessStartInfo psi)
    {
        if (Executable == "dotnet")
            return;
        psi.Environment["DOTNET_ROOT"] = Root;
        var path = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH");
        if (path is null || !path.Split(':').Contains(Root))
            psi.Environment["PATH"] = Root + ":" + path;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
    }
}
