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
    readonly Func<IEnumerable<ExplorerNode>>? _source;

    public ExplorerNode(NodeKind kind, string name, string? path = null, Func<IEnumerable<ExplorerNode>>? loader = null)
    {
        Kind = kind;
        Name = name;
        Path = path;
        _loader = loader;
        _source = loader;
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

    /// <summary>Supplies a file's git status letter (M/A/D/U); set by the view model when the solution is a repository.</summary>
    public static Func<string, string?>? GitStatusProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitBadgeColor))]
    public partial string? GitBadge { get; set; }

    public string GitBadgeColor => GitBadge switch
    {
        "A" or "U" => "#73C991",
        "D" => "#C74E39",
        "C" => "#E4676B",
        _ => "#E2C08D",
    };

    /// <summary>Re-reads git badges for this node and every child that has already been loaded.</summary>
    public void RefreshGitBadges()
    {
        GitBadge = Kind == NodeKind.File && Path is not null ? GitStatusProvider?.Invoke(Path) : null;
        if (_loader is not null)
            return;
        foreach (var child in Children)
            child.RefreshGitBadges();
    }

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
        NodeKind.Folder when Name == "Properties" => "PropertiesFolder",
        NodeKind.Folder when Name == "wwwroot" => "WebFolder",
        NodeKind.Folder or NodeKind.SolutionFolder or NodeKind.DependencyGroup => IsExpanded ? "FolderOpen" : "Folder",
        NodeKind.Project => System.IO.Path.GetExtension(Path ?? "").ToLowerInvariant() switch
        {
            ".fsproj" => "FsProject",
            ".vbproj" => "VbProject",
            _ => "Project",
        },
        _ => Kind.ToString(),
    };

    /// <summary>
    /// The icon Solution Explorer shows: Material Icon Theme icons for files, folders and the solution
    /// ("material:name"); projects keep VS's language project icons so they stand apart from the solution and from
    /// source files; dependencies, packages and frameworks keep the VS icons too.
    /// </summary>
    public string ThemeIcon => Kind switch
    {
        NodeKind.File => "material:" + Views.MaterialIcons.ForFile(Name),
        NodeKind.Folder or NodeKind.SolutionFolder => "material:" + Views.MaterialIcons.ForFolder(Name, IsExpanded),
        NodeKind.Solution when Path is not null => "material:" + Views.MaterialIcons.ForFile(Path),
        _ => IconKey,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey), nameof(ThemeIcon))]
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
        {
            n.RefreshGitBadges();
            Children.Add(n);
        }
    }

    /// <summary>Whether the children have been read (a lazy folder that was never expanded has not).</summary>
    public bool IsLoaded => _loader is null;

    /// <summary>Being renamed in place (F2): the row shows a text box with <see cref="EditName"/>.</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    /// <summary>
    /// Re-reads a loaded folder or project from disk after files were added, renamed or deleted. Nodes that are still
    /// there are kept, with their expansion and loaded children; a folder that was never expanded stays lazy.
    /// </summary>
    public void Reload()
    {
        if (_source is null || _loader is not null)
            return;
        List<ExplorerNode> fresh;
        try
        {
            fresh = _source().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Merge(Children, fresh);
    }

    /// <summary>Makes <paramref name="target"/> list <paramref name="fresh"/>'s nodes, reusing the ones it already has.</summary>
    static void Merge(ObservableCollection<ExplorerNode> target, List<ExplorerNode> fresh)
    {
        var existing = new Dictionary<(NodeKind, string), ExplorerNode>();
        foreach (var node in target)
            existing.TryAdd(node.Key, node);
        var wanted = new List<ExplorerNode>(fresh.Count);
        foreach (var node in fresh)
        {
            if (existing.TryGetValue(node.Key, out var kept))
            {
                // A file's nested files (X.razor.cs under X.razor) are built with it, not loaded lazily.
                if (kept.Kind == NodeKind.File)
                    Merge(kept.Children, node.Children.ToList());
                wanted.Add(kept);
            }
            else
            {
                node.RefreshGitBadges();
                wanted.Add(node);
            }
        }
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var current = target.IndexOf(wanted[i]);
            if (current == i)
                continue;
            if (current >= 0)
                target.Move(current, i);
            else
                target.Insert(i, wanted[i]);
        }
    }

    (NodeKind, string) Key => (Kind, Path ?? Name);

    /// <summary>The loaded project or folder node showing <paramref name="directory"/>, without loading anything.</summary>
    public ExplorerNode? FindLoadedFolder(string directory)
    {
        if (Kind is NodeKind.Folder && Path == directory || Kind is NodeKind.Project && Path is not null && DirectoryOf(this) == directory)
            return this;
        if (!IsLoaded || Kind is NodeKind.File or NodeKind.Dependencies or NodeKind.DependencyGroup or NodeKind.Placeholder)
            return null;
        if (Kind is NodeKind.Folder or NodeKind.Project && Path is not null && !directory.StartsWith(DirectoryOf(this) + "/", StringComparison.Ordinal))
            return null;
        foreach (var child in Children)
            if (child.FindLoadedFolder(directory) is { } hit)
                return hit;
        return null;
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
