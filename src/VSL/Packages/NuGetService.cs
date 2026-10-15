using System.Text.Json;
using VSL.Build;

namespace VSL.Packages;

public sealed record PackageSearchResult(string Id, string LatestVersion, long TotalDownloads, string? Owners, string? Description, string? ProjectUrl, string Source);

public sealed record InstalledPackage(string ProjectPath, string Id, string RequestedVersion, string ResolvedVersion, string? LatestVersion);

/// <summary>
/// NuGet operations through the dotnet CLI (package search, list package, add/remove package), run in the
/// solution folder so NuGet.Config, private feeds and their credentials apply exactly as in a build.
/// </summary>
public sealed class NuGetService(string workingDirectory, Action<string>? log = null)
{
    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string term, bool prerelease, int take, CancellationToken ct)
    {
        List<string> args = ["package", "search", term, "--format", "json", "--take", take.ToString(), "--verbosity", "detailed"];
        if (prerelease)
            args.Add("--prerelease");
        using var doc = await RunJsonAsync(args, ct);
        var results = new List<PackageSearchResult>();
        foreach (var source in doc.RootElement.GetProperty("searchResult").EnumerateArray())
        {
            var sourceName = source.GetProperty("sourceName").GetString() ?? "";
            if (!source.TryGetProperty("packages", out var packages))
                continue;
            foreach (var p in packages.EnumerateArray())
            {
                string? Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var id = Str("id") ?? "";
                if (results.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                results.Add(new PackageSearchResult(id, Str("latestVersion") ?? Str("version") ?? "",
                    p.TryGetProperty("totalDownloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                    Str("owners"), Str("description"), Str("projectUrl"), sourceName));
            }
        }
        return results;
    }

    /// <summary>All published versions of a package, newest first.</summary>
    public async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct)
    {
        List<string> args = ["package", "search", id, "--exact-match", "--format", "json"];
        if (prerelease)
            args.Add("--prerelease");
        using var doc = await RunJsonAsync(args, ct);
        var versions = new List<string>();
        foreach (var source in doc.RootElement.GetProperty("searchResult").EnumerateArray())
            if (source.TryGetProperty("packages", out var packages))
                foreach (var p in packages.EnumerateArray())
                    if (p.TryGetProperty("version", out var v) && v.GetString() is { } version && !versions.Contains(version))
                        versions.Add(version);
        return versions.OrderByDescending(ParseVersion).ToList();
    }

    /// <summary>Top-level packages of each project; with <paramref name="outdated"/>, only those with a newer version.</summary>
    public async Task<IReadOnlyList<InstalledPackage>> ListAsync(string target, bool outdated, bool prerelease, CancellationToken ct)
    {
        List<string> args = ["list", target, "package", "--format", "json"];
        if (outdated)
            args.Add("--outdated");
        if (outdated && prerelease)
            args.Add("--include-prerelease");
        using var doc = await RunJsonAsync(args, ct);
        var list = new List<InstalledPackage>();
        if (!doc.RootElement.TryGetProperty("projects", out var projects))
            return list;
        foreach (var project in projects.EnumerateArray())
        {
            var path = project.GetProperty("path").GetString() ?? "";
            if (!project.TryGetProperty("frameworks", out var frameworks))
                continue;
            foreach (var framework in frameworks.EnumerateArray())
            {
                if (!framework.TryGetProperty("topLevelPackages", out var packages))
                    continue;
                foreach (var p in packages.EnumerateArray())
                {
                    string? Str(string name) => p.TryGetProperty(name, out var v) ? v.GetString() : null;
                    var id = Str("id") ?? "";
                    if (list.Any(x => x.ProjectPath == path && x.Id == id))
                        continue;
                    list.Add(new InstalledPackage(path, id, Str("requestedVersion") ?? "", Str("resolvedVersion") ?? "", Str("latestVersion")));
                }
            }
        }
        return list;
    }

    public Task<(bool Success, string Output)> InstallAsync(string project, string id, string version, CancellationToken ct) =>
        RunAsync(["add", project, "package", id, "--version", version], ct);

    public Task<(bool Success, string Output)> UninstallAsync(string project, string id, CancellationToken ct) =>
        RunAsync(["remove", project, "package", id], ct);

    static Version ParseVersion(string v)
    {
        var core = v.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
    }

    async Task<JsonDocument> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (success, output) = await RunAsync(args, ct, quiet: true);
        var start = output.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException(success ? "Unexpected output from dotnet." : output.Trim());
        try
        {
            return JsonDocument.Parse(output[start..]);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(output.Trim());
        }
    }

    async Task<(bool Success, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool quiet = false)
    {
        if (!quiet)
            log?.Invoke("> dotnet " + string.Join(' ', args));
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, workingDirectory, line =>
        {
            lock (lines)
                lines.Add(line);
            if (!quiet)
                log?.Invoke(line);
        });
        var exit = await runner.WaitAsync(ct);
        lock (lines)
            return (exit == 0, string.Join('\n', lines));
    }
}
