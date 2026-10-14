using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VSL.Workspace;

public enum NodeKind
{
    Solution,
    SolutionFolder,
    Project,
    Dependencies,
    DependencyGroup,
    Package,
    ProjectReference,
    Framework,
    Folder,
    File,
    Placeholder,
}

/// <summary>A Solution Explorer tree node. Folder contents load lazily on first expand.</summary>
public partial class ExplorerNode : ObservableObject
{
    Func<IEnumerable<ExplorerNode>>? _loader;

    public ExplorerNode(NodeKind kind, string name, string? path = null, Func<IEnumerable<ExplorerNode>>? loader = null)
    {
        Kind = kind;
        Name = name;
        Path = path;
        _loader = loader;
        if (loader is not null)
            Children.Add(new ExplorerNode(NodeKind.Placeholder, "Loading..."));
    }

    public NodeKind Kind { get; }
    public string Name { get; }
    public string? Path { get; }
    public string? Detail { get; init; }
    public ProjectInfo? Project { get; init; }
    public ObservableCollection<ExplorerNode> Children { get; } = [];

    public string DisplayName => Detail is null ? Name : $"{Name} {Detail}";

    /// <summary>Icon key resolved by the view (see Views/Icons.cs).</summary>
    public string IconKey => Kind switch
    {
        NodeKind.File => System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
        {
            ".cs" => "cs",
            ".razor" or ".cshtml" => "razor",
            ".css" or ".scss" or ".less" => "css",
            ".js" or ".mjs" or ".ts" => "js",
            ".json" => "json",
            ".xml" or ".csproj" or ".props" or ".targets" or ".resx" or ".config" => "xml",
            ".sql" => "sql",
            ".md" => "md",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".webp" => "image",
            ".html" or ".htm" => "html",
            ".proto" => "proto",
            ".yml" or ".yaml" => "yaml",
            ".xlsx" or ".docx" or ".pdf" => "binary",
            _ => "file",
        },
        _ => Kind.ToString(),
    };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            EnsureLoaded();
    }

    public void EnsureLoaded()
    {
        var loader = Interlocked.Exchange(ref _loader, null);
        if (loader is null)
            return;
        List<ExplorerNode> nodes;
        try
        {
            nodes = loader().ToList();
        }
        catch (IOException ex)
        {
            nodes = [new ExplorerNode(NodeKind.Placeholder, ex.Message)];
        }
        catch (UnauthorizedAccessException ex)
        {
            nodes = [new ExplorerNode(NodeKind.Placeholder, ex.Message)];
        }
        Children.Clear();
        foreach (var n in nodes)
            Children.Add(n);
    }

    /// <summary>Depth-first search that forces lazy folders to load along the way.</summary>
    public ExplorerNode? FindByPath(string path)
    {
        if (Path is not null && string.Equals(Path, path, StringComparison.Ordinal))
            return this;
        if (Kind is NodeKind.Dependencies or NodeKind.DependencyGroup or NodeKind.Placeholder)
            return null;
        if (Path is not null && Kind is NodeKind.Folder or NodeKind.Project && !path.StartsWith(DirectoryOf(this) + "/", StringComparison.Ordinal))
            return null;
        EnsureLoaded();
        foreach (var child in Children)
            if (child.FindByPath(path) is { } hit)
                return hit;
        return null;
    }

    static string DirectoryOf(ExplorerNode node) =>
        node.Kind == NodeKind.Project ? System.IO.Path.GetDirectoryName(node.Path!)! : node.Path!;
}
