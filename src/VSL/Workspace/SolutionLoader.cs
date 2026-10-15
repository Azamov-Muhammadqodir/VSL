using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace VSL.Workspace;

public sealed class SolutionInfo
{
    /// <summary>The .sln/.slnx, or the project file when a project was opened on its own (as VS allows).</summary>
    public required string FilePath { get; init; }

    public bool IsProjectOnly => SolutionLoader.IsProjectFile(FilePath);
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public required ExplorerNode Root { get; init; }
}

/// <summary>Reads .sln/.slnx with Microsoft's own solution parser and builds the Solution Explorer tree.</summary>
public static class SolutionLoader
{
    static readonly HashSet<string> HiddenDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static bool IsProjectFile(string path) => ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>What File > Open accepts: a solution, or a single project.</summary>
    public static bool CanOpen(string path) => IsSolutionFile(path) || IsProjectFile(path);

    public static async Task<SolutionInfo> LoadAsync(string solutionPath, CancellationToken ct = default)
    {
        if (IsProjectFile(solutionPath))
            return LoadProject(solutionPath);
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new NotSupportedException($"Unsupported solution format: {solutionPath}");
        var model = await serializer.OpenAsync(solutionPath, ct);
        var dir = Path.GetDirectoryName(solutionPath)!;

        var projects = model.SolutionProjects
            .Select(p => ProjectInfo.Load(p.ActualDisplayName, Path.GetFullPath(Path.Combine(dir, p.FilePath.Replace('\\', '/')))))
            .ToList();
        var byPath = projects.ToDictionary(p => p.FilePath);

        IEnumerable<ExplorerNode> ChildrenOf(SolutionFolderModel? folder)
        {
            var folders = model.SolutionFolders.Where(f => f.Parent == folder)
                .OrderBy(f => f.ActualDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(f => FolderNode(f));
            var projs = model.SolutionProjects.Where(p => p.Parent == folder)
                .OrderBy(p => p.ActualDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => ProjectNode(byPath[Path.GetFullPath(Path.Combine(dir, p.FilePath.Replace('\\', '/')))]));
            return folders.Concat(projs);
        }

        ExplorerNode FolderNode(SolutionFolderModel f)
        {
            var node = new ExplorerNode(NodeKind.SolutionFolder, f.ActualDisplayName);
            foreach (var child in ChildrenOf(f))
                node.Children.Add(child);
            foreach (var file in f.Files ?? [])
            {
                var full = Path.GetFullPath(Path.Combine(dir, file.Replace('\\', '/')));
                node.Children.Add(new ExplorerNode(NodeKind.File, Path.GetFileName(full), full));
            }
            return node;
        }

        var root = new ExplorerNode(NodeKind.Solution, $"Solution '{Path.GetFileNameWithoutExtension(solutionPath)}'", solutionPath)
        {
            Detail = $"({projects.Count} of {projects.Count} projects)",
        };
        foreach (var child in ChildrenOf(null))
            root.Children.Add(child);
        root.IsExpanded = true;

        return new SolutionInfo { FilePath = solutionPath, Projects = projects, Root = root };
    }

    /// <summary>A project opened without a solution: VS shows it under an implicit solution of the same name.</summary>
    static SolutionInfo LoadProject(string projectPath)
    {
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("The project file was not found.", projectPath);
        var project = ProjectInfo.Load(Path.GetFileNameWithoutExtension(projectPath), Path.GetFullPath(projectPath));
        var root = new ExplorerNode(NodeKind.Solution, $"Solution '{project.Name}'", projectPath) { Detail = "(1 of 1 project)" };
        root.Children.Add(ProjectNode(project));
        root.IsExpanded = true;
        return new SolutionInfo { FilePath = projectPath, Projects = [project], Root = root };
    }

    public static ExplorerNode ProjectNode(ProjectInfo project) =>
        new(NodeKind.Project, project.Name, project.FilePath, () => ProjectChildren(project)) { Project = project };

    static IEnumerable<ExplorerNode> ProjectChildren(ProjectInfo project)
    {
        yield return new ExplorerNode(NodeKind.Dependencies, "Dependencies", null, () => DependencyGroups(project));
        foreach (var node in DirectoryChildren(project.Directory, isProjectRoot: true))
            yield return node;
    }

    static IEnumerable<ExplorerNode> DependencyGroups(ProjectInfo project)
    {
        if (project.TargetFrameworks.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Frameworks", null, () =>
                project.TargetFrameworks.Select(t => new ExplorerNode(NodeKind.Framework, FrameworkName(project, t))));
        if (project.Packages.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Packages", null, () =>
                project.Packages.Select(p => new ExplorerNode(NodeKind.Package, p.Name) { Detail = p.Version is { Length: > 0 } v ? $"({v})" : null }));
        if (project.ProjectReferences.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Projects", null, () =>
                project.ProjectReferences.Select(p => new ExplorerNode(NodeKind.ProjectReference, Path.GetFileNameWithoutExtension(p), p)));
    }

    static string FrameworkName(ProjectInfo project, string tfm) =>
        project.IsWeb ? $"Microsoft.AspNetCore.App ({tfm})" : $"Microsoft.NETCore.App ({tfm})";

    public static IEnumerable<ExplorerNode> DirectoryChildren(string directory, bool isProjectRoot = false)
    {
        var dirs = new DirectoryInfo(directory).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && !(isProjectRoot && HiddenDirectories.Contains(d.Name)) && d.Name != "node_modules")
            .OrderBy(d => SpecialFolderRank(d.Name, isProjectRoot))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ExplorerNode(NodeKind.Folder, d.Name, d.FullName, () => DirectoryChildren(d.FullName)));

        var fileNames = new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.') && !(isProjectRoot && f.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var files = FileNesting.Nest(fileNames).Select(entry =>
        {
            var node = new ExplorerNode(NodeKind.File, entry.Name, Path.Combine(directory, entry.Name));
            foreach (var child in entry.Children)
                node.Children.Add(new ExplorerNode(NodeKind.File, child, Path.Combine(directory, child)));
            return node;
        });
        return dirs.Concat(files);
    }

    static int SpecialFolderRank(string name, bool isProjectRoot) => !isProjectRoot ? 0 : name switch
    {
        "Properties" => 0,
        "wwwroot" => 1,
        _ => 2,
    };
}
