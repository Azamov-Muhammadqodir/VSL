using System.Diagnostics;

namespace VSL.Lsp;

/// <summary>Where the Roslyn language server lives on disk.</summary>
public sealed record RoslynServerInstall(string Executable, string Directory)
{
    public string CSharpDesignTimeTargets => Path.Combine(Directory, "Targets", "Microsoft.CSharpExtension.DesignTime.targets");
}

/// <summary>
/// Finds or installs <c>roslyn-language-server</c> (the MIT-licensed C#/Razor server that ships on nuget.org as a
/// dotnet tool) into ~/.local/share/VSL/roslyn/&lt;version&gt;.
/// </summary>
public static class RoslynServerLocator
{
    public const string PackageId = "roslyn-language-server";
    public const string Version = "5.13.0-1.26507.4";

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSL", "roslyn", Version);

    public static RoslynServerInstall? Find()
    {
        var overrideDir = Environment.GetEnvironmentVariable("VSL_ROSLYN_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
            return FromDirectory(overrideDir);
        if (!System.IO.Directory.Exists(InstallRoot))
            return null;
        var dll = System.IO.Directory.EnumerateFiles(InstallRoot, "Microsoft.CodeAnalysis.LanguageServer.dll", SearchOption.AllDirectories).FirstOrDefault();
        return dll is null ? null : FromDirectory(Path.GetDirectoryName(dll)!);
    }

    static RoslynServerInstall? FromDirectory(string dir)
    {
        var exe = Path.Combine(dir, "roslyn-language-server");
        return File.Exists(exe) ? new RoslynServerInstall(exe, dir) : null;
    }

    public static async Task<RoslynServerInstall> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;

        log($"Installing {PackageId} {Version} into {InstallRoot} ...");
        System.IO.Directory.CreateDirectory(InstallRoot);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Run outside any project so a repo's NuGet.Config cannot redirect the package source.
            WorkingDirectory = InstallRoot,
        };
        foreach (var a in new[] { "tool", "install", PackageId, "--version", Version, "--tool-path", InstallRoot })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        log((await stdout).Trim());
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dotnet tool install failed ({p.ExitCode}): {(await stderr).Trim()}");
        return Find() ?? throw new InvalidOperationException("roslyn-language-server installed but its files were not found.");
    }
}
