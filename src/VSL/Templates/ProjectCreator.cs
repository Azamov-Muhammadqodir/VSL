using VSL.Build;

namespace VSL.Templates;

/// <summary>Everything the "Create a new project" pages collect.</summary>
public sealed record NewProjectRequest(
    ProjectTemplate Template,
    string ProjectName,
    string Location,
    string SolutionName,
    bool SolutionInSameDirectory,
    IReadOnlyList<(TemplateOption Option, string Value)> Options)
{
    public string SolutionDirectory => Path.Combine(Location, SolutionInSameDirectory ? ProjectName : SolutionName);

    public string ProjectDirectory => SolutionInSameDirectory ? SolutionDirectory : Path.Combine(SolutionDirectory, ProjectName);
}

/// <summary>Creates a project from a template with <c>dotnet new</c> and puts it in a solution, like VS's new project wizard.</summary>
public static class ProjectCreator
{
    static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".esproj"];

    /// <summary>Why the request cannot be created, or null when it can.</summary>
    public static string? Validate(string projectName, string location, string solutionName, bool sameDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "Enter a project name.";
        if (!IsValidName(projectName))
            return "The project name contains characters that are not allowed.";
        if (!sameDirectory && string.IsNullOrWhiteSpace(solutionName))
            return "Enter a solution name.";
        if (!sameDirectory && !IsValidName(solutionName))
            return "The solution name contains characters that are not allowed.";
        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathRooted(location))
            return "Enter a full path for the location.";
        var target = Path.Combine(location, sameDirectory ? projectName : solutionName);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return $"The directory \"{target}\" already exists and is not empty.";
        return null;
    }

    static bool IsValidName(string name) =>
        name.Trim() == name && name is not ("." or "..") && name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    /// <summary>Runs the template and returns the solution to open.</summary>
    public static async Task<string> CreateAsync(NewProjectRequest request, Action<string> log, CancellationToken ct = default, bool restore = true)
    {
        Directory.CreateDirectory(request.Location);
        List<string> args = ["new", request.Template.ShortName, "--name", request.ProjectName, "--output", request.ProjectDirectory];
        if (!restore)
            args.Add("--no-restore");
        if (request.Template.Language is { } language)
            args.AddRange(["--language", language]);
        foreach (var (option, value) in request.Options)
            args.AddRange(option.Arguments(value));
        await RunAsync(args, request.Location, log, ct);

        // Multi-project templates (e.g. Blazor with WebAssembly) bring their own solution.
        var templateSolution = Solutions(request.ProjectDirectory).FirstOrDefault();
        if (request.SolutionInSameDirectory && templateSolution is not null)
            return templateSolution;

        var solutionName = request.SolutionInSameDirectory ? request.ProjectName : request.SolutionName;
        await RunAsync(["new", "sln", "--name", solutionName, "--output", request.SolutionDirectory], request.Location, log, ct);
        var solution = Solutions(request.SolutionDirectory).FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == solutionName)
            ?? throw new InvalidOperationException("dotnet new sln did not create a solution file.");
        if (templateSolution is not null && templateSolution != solution)
            File.Delete(templateSolution);

        var projects = Directory.EnumerateFiles(request.ProjectDirectory, "*", SearchOption.AllDirectories)
            .Where(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (projects.Count > 0)
            await RunAsync(["sln", solution, "add", "--in-root", .. projects], request.SolutionDirectory, log, ct);
        return solution;
    }

    static IEnumerable<string> Solutions(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [];

    static async Task RunAsync(IReadOnlyList<string> args, string workingDirectory, Action<string> log, CancellationToken ct)
    {
        log("> dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, workingDirectory, line =>
        {
            lock (lines)
                lines.Add(line);
            log(line);
        });
        if (await runner.WaitAsync(ct) != 0)
        {
            lock (lines)
                throw new InvalidOperationException(lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"dotnet {args[0]} {args[1]} failed.");
        }
    }
}
