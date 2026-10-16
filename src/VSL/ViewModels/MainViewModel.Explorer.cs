using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using VSL.Build;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>What Add New Item returns: the template and the name typed for the file.</summary>
public sealed record NewItemChoice(ItemTemplate Template, string Name);

/// <summary>
/// Solution Explorer's file operations, as Visual Studio's context menu offers them: Add (New Item, Existing Item,
/// New Folder, Class), Rename and Delete. SDK-style projects include their files by wildcard, so these work on disk.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The view starts renaming a node in place (a new folder is named this way, as in VS).</summary>
    public event Action<ExplorerNode>? ExplorerRenameRequested;

    /// <summary>The view selects and reveals a node (a new or renamed item).</summary>
    public event Action<ExplorerNode>? ExplorerSelectRequested;

    /// <summary>Moves a file or folder to the desktop's trash; false when that is not possible (replaced in tests).</summary>
    public Func<string, Task<bool>> MoveToTrash { get; set; } = MoveToTrashAsync;

    /// <summary>The folder a node adds into: a project's or solution's folder, a folder, or a file's folder.</summary>
    public static string? FolderOf(ExplorerNode? node) => node switch
    {
        { Kind: NodeKind.Folder, Path: { } path } => path,
        { Kind: NodeKind.Project or NodeKind.Solution or NodeKind.File, Path: { } path } => Path.GetDirectoryName(path),
        _ => null,
    };

    /// <summary>The innermost project whose folder holds <paramref name="path"/>.</summary>
    public ProjectInfo? ProjectContaining(string path) =>
        Solution?.Projects.Where(p => path == p.Directory || path.StartsWith(p.Directory + "/", StringComparison.Ordinal))
            .MaxBy(p => p.Directory.Length);

    /// <summary>Add > New Item... (Ctrl+Shift+A): the Add New Item dialog, then the new file opens.</summary>
    public async Task<string?> AddNewItemAsync(ExplorerNode? node, ItemTemplate? template = null)
    {
        if (Dialogs is null || FolderOf(node ?? ExplorerTarget()) is not { } folder)
            return null;
        var owner = ProjectContaining(folder)?.Name ?? Solution?.Name ?? "";
        var choice = await Dialogs.ChooseNewItemAsync($"Add New Item - {owner}", folder, template ?? ItemTemplates.Class);
        return choice is null ? null : CreateItem(folder, choice.Template, choice.Name);
    }

    /// <summary>Writes a new item from a template, shows it in Solution Explorer and opens it.</summary>
    public string? CreateItem(string folder, ItemTemplate template, string name)
    {
        var fileName = ItemTemplates.FileName(template, name);
        if (ItemTemplates.Validate(folder, fileName) is { } error)
        {
            SetStatus(error);
            return null;
        }
        var path = Path.Combine(folder, fileName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, ItemTemplates.Render(template, fileName, folder, ProjectContaining(folder)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message);
            return null;
        }
        ShowInExplorer(path);
        OpenDocument(path);
        return path;
    }

    /// <summary>Add > New Folder: "NewFolder1" (or the next free number), then renamed in place.</summary>
    public ExplorerNode? AddNewFolder(ExplorerNode? node)
    {
        if (FolderOf(node ?? ExplorerTarget()) is not { } parent)
            return null;
        var path = Enumerable.Range(1, 1000).Select(i => Path.Combine(parent, "NewFolder" + i))
            .First(p => !Directory.Exists(p) && !File.Exists(p));
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message);
            return null;
        }
        var created = ShowInExplorer(path);
        if (created is not null)
            ExplorerRenameRequested?.Invoke(created);
        return created;
    }

    /// <summary>Add > Existing Item... (Shift+Alt+A): copies the chosen files into the folder.</summary>
    public async Task<IReadOnlyList<string>> AddExistingItemsAsync(ExplorerNode? node)
    {
        if (Dialogs is null || FolderOf(node ?? ExplorerTarget()) is not { } folder)
            return [];
        var added = new List<string>();
        foreach (var file in await Dialogs.PickFilesAsync("Add Existing Item"))
        {
            var target = Path.Combine(folder, Path.GetFileName(file));
            if (target == file)
                continue;
            if (File.Exists(target) && await Dialogs.ConfirmAsync("Destination File Exists",
                    $"A file with the name '{Path.GetFileName(file)}' already exists. Do you want to replace it?") != true)
                continue;
            try
            {
                File.Copy(file, target, overwrite: true);
                added.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message);
            }
        }
        if (added.Count > 0)
            ShowInExplorer(added[^1]);
        return added;
    }

    /// <summary>Starts renaming a file or folder in place (F2).</summary>
    public void BeginRename(ExplorerNode? node)
    {
        if (node is { Kind: NodeKind.File or NodeKind.Folder, Path: not null })
            ExplorerRenameRequested?.Invoke(node);
    }

    /// <summary>
    /// Renames a file or folder on disk. A file's nested files follow it (X.razor.cs with X.razor), and open
    /// documents move to the new name, keeping unsaved edits.
    /// </summary>
    public bool Rename(ExplorerNode node, string newName)
    {
        newName = newName.Trim();
        if (node.Path is not { } path || node.Kind is not (NodeKind.File or NodeKind.Folder) || newName == node.Name)
            return false;
        var directory = Path.GetDirectoryName(path)!;
        if (ItemTemplates.Validate(directory, newName) is { } error)
        {
            SetStatus(error);
            return false;
        }
        var moves = new List<(string From, string To)> { (path, Path.Combine(directory, newName)) };
        foreach (var nested in node.Children.Where(c => c.Kind == NodeKind.File && c.Path is not null && c.Name.StartsWith(node.Name, StringComparison.Ordinal)))
            moves.Add((nested.Path!, Path.Combine(directory, newName + nested.Name[node.Name.Length..])));

        // Open documents at or under the moved paths, reopened at the new place afterwards.
        string? MovedTo(string file)
        {
            foreach (var (from, to) in moves)
            {
                if (file == from)
                    return to;
                if (file.StartsWith(from + "/", StringComparison.Ordinal))
                    return to + file[from.Length..];
            }
            return null;
        }
        var reopen = Editors.Select(d => (Doc: d, To: MovedTo(d.FilePath))).Where(x => x.To is not null).ToList();
        var active = ActiveDocument;
        try
        {
            foreach (var (from, to) in moves)
            {
                if (Directory.Exists(from))
                    Directory.Move(from, to);
                else
                    File.Move(from, to);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message);
            return false;
        }
        foreach (var (doc, to) in reopen)
        {
            var unsaved = doc.IsDirty ? doc.Host.Document.Text : null;
            var wasActive = doc == active;
            var index = Documents.IndexOf(doc);
            Documents.Remove(doc);
            doc.Dispose();
            if (OpenDocument(to!, activate: wasActive) is { } moved)
            {
                Documents.Move(Documents.IndexOf(moved), Math.Clamp(index, 0, Documents.Count - 1));
                if (unsaved is not null)
                    moved.Host.Document.Text = unsaved;
            }
        }
        if (ActiveDocument is null && Documents.Count > 0)
            ActiveDocument = Documents[0];
        ShowInExplorer(moves[0].To);
        SaveSession();
        return true;
    }

    /// <summary>Delete (Del): to the trash when the desktop has one (VS uses the Recycle Bin), after confirming.</summary>
    public async Task<bool> DeleteAsync(ExplorerNode? node)
    {
        if (Dialogs is null || node is not { Kind: NodeKind.File or NodeKind.Folder, Path: { } path })
            return false;
        if (await Dialogs.ConfirmAsync("VSL", $"'{node.Name}' will be deleted.") != true)
            return false;
        var paths = new List<string> { path };
        paths.AddRange(node.Children.Where(c => c.Kind == NodeKind.File && c.Path is not null).Select(c => c.Path!));
        foreach (var doc in Editors.Where(d => paths.Any(p => d.FilePath == p || d.FilePath.StartsWith(p + "/", StringComparison.Ordinal))).ToList())
        {
            Documents.Remove(doc);
            doc.Dispose();
        }
        if (ActiveDocument is not null && !Documents.Contains(ActiveDocument))
            ActiveDocument = Documents.FirstOrDefault();
        foreach (var p in paths)
        {
            try
            {
                if (!await MoveToTrash(p))
                {
                    if (Directory.Exists(p))
                        Directory.Delete(p, recursive: true);
                    else if (File.Exists(p))
                        File.Delete(p);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message);
                return false;
            }
        }
        ReloadExplorerFolders(paths);
        SaveSession();
        return true;
    }

    /// <summary>Edit Project File: the .csproj in the editor.</summary>
    public void EditProjectFile(ProjectInfo? project)
    {
        if (project is not null)
            OpenDocument(project.FilePath);
    }

    /// <summary>Re-reads the loaded Solution Explorer folders that hold <paramref name="changedPaths"/>.</summary>
    public void ReloadExplorerFolders(IEnumerable<string> changedPaths)
    {
        if (ExplorerRoots.FirstOrDefault() is not { } root)
            return;
        foreach (var directory in changedPaths.Select(Path.GetDirectoryName).OfType<string>().Distinct())
            root.FindLoadedFolder(directory)?.Reload();
    }

    /// <summary>Re-reads the item's folder, expands down to it and selects it.</summary>
    ExplorerNode? ShowInExplorer(string path)
    {
        ReloadExplorerFolders([path]);
        if (ExplorerRoots.FirstOrDefault() is not { } root || root.FindByPath(path) is not { } node)
            return null;
        ExpandAncestors(root, node);
        ExplorerSelectRequested?.Invoke(node);
        return node;
    }

    /// <summary>Where Add goes without a selected node: the active document's folder, else the startup project's.</summary>
    ExplorerNode? ExplorerTarget()
    {
        if (ExplorerRoots.FirstOrDefault() is not { } root)
            return null;
        if (ActiveEditor is { } editor && root.FindByPath(Path.GetDirectoryName(editor.FilePath)!) is { } folder)
            return folder;
        return StartupProject is { } project ? root.FindByPath(project.FilePath) : null;
    }

    [RelayCommand]
    Task AddNewItem(ExplorerNode? node) => AddNewItemAsync(node);

    [RelayCommand]
    Task AddExistingItem(ExplorerNode? node) => AddExistingItemsAsync(node);

    static async Task<bool> MoveToTrashAsync(string path)
    {
        try
        {
            using var gio = Process.Start(new ProcessStartInfo("gio", ["trash", path])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (gio is null)
                return false;
            await gio.WaitForExitAsync();
            return gio.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No gio: deleted for good.
            return false;
        }
    }
}
