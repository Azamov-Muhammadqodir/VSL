using System.Text.Json;

namespace VSL.Services;

/// <summary>
/// Everything VSL remembers about one solution, kept next to it in .vs/&lt;Solution&gt;/VSL/state.json as Visual
/// Studio keeps its .suo in .vs/&lt;Solution&gt;/v17: open documents with their caret, scroll position, collapsed blocks and
/// pins, the active document, startup project, launch profile and configuration, breakpoints and watches, the
/// Solution Explorer nodes that were expanded and the Claude Code conversation. Paths are stored relative to the
/// solution folder, so the state moves with it. In memory they are full paths.
/// </summary>
public sealed class SolutionState
{
    public List<DocumentState> Documents { get; set; } = [];
    public string? ActiveDocument { get; set; }
    public string? StartupProject { get; set; }
    public string? LaunchProfile { get; set; }
    public string Configuration { get; set; } = "Debug";
    public Dictionary<string, List<int>> Breakpoints { get; set; } = [];
    public List<string> Watches { get; set; } = [];

    /// <summary>Solution Explorer nodes that were expanded (paths, or "folder:Name" for solution folders).</summary>
    public List<string> ExpandedNodes { get; set; } = [];

    public string? SelectedNode { get; set; }

    /// <summary>The Claude Code conversation that was open (its session id); it comes back with the solution.</summary>
    public string? ClaudeConversation { get; set; }

    public sealed class DocumentState
    {
        public string Path { get; set; } = "";
        public int CaretLine { get; set; } = 1;
        public int CaretColumn { get; set; } = 1;
        public int FirstVisibleLine { get; set; } = 1;
        public bool Pinned { get; set; }

        /// <summary>First lines of the blocks that were collapsed.</summary>
        public List<int> Collapsed { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>.vs/&lt;Solution&gt;/VSL/state.json in the solution's folder (a project opened alone: the project's).</summary>
    public static string FileFor(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".vs", Path.GetFileNameWithoutExtension(solutionPath), "VSL", "state.json");

    /// <summary>The saved state; else what older VSL versions kept in settings.json (moved out of there); else empty.</summary>
    public static SolutionState Load(string solutionPath, Settings settings)
    {
        var folder = Path.GetDirectoryName(solutionPath)!;
        try
        {
            var file = FileFor(solutionPath);
            if (File.Exists(file) && JsonSerializer.Deserialize<SolutionState>(File.ReadAllText(file)) is { } saved)
                return saved.Resolve(folder, toFull: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable state file never blocks opening the solution.
        }
        if (settings.Solutions.Remove(solutionPath, out var legacy))
        {
            settings.Save();
            return new SolutionState
            {
                Documents = legacy.OpenDocuments.Select(p => new DocumentState { Path = p }).ToList(),
                ActiveDocument = legacy.ActiveDocument,
                StartupProject = legacy.StartupProject,
                LaunchProfile = legacy.LaunchProfile,
                Configuration = legacy.Configuration,
                Breakpoints = legacy.Breakpoints,
                Watches = legacy.Watches,
            };
        }
        return new SolutionState();
    }

    /// <summary>Writes the state (replacing the file in one step); false when the folder cannot be written.</summary>
    public bool Save(string solutionPath)
    {
        var file = FileFor(solutionPath);
        try
        {
            var vs = Path.Combine(Path.GetDirectoryName(solutionPath)!, ".vs");
            var created = !Directory.Exists(vs);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Keeps .vs out of git status in repositories whose .gitignore does not list it (Visual Studio's does).
            if (created)
                File.WriteAllText(Path.Combine(vs, ".gitignore"), "*\n");
            var copy = JsonSerializer.Deserialize<SolutionState>(JsonSerializer.Serialize(this, Json))!
                .Resolve(Path.GetDirectoryName(solutionPath)!, toFull: false);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turns every path to full (after reading) or relative to <paramref name="folder"/> (before writing).</summary>
    SolutionState Resolve(string folder, bool toFull)
    {
        string? Convert(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("folder:", StringComparison.Ordinal))
                return path;
            return toFull ? Path.GetFullPath(Path.Combine(folder, path)) : Path.GetRelativePath(folder, path);
        }
        foreach (var doc in Documents)
            doc.Path = Convert(doc.Path)!;
        ActiveDocument = Convert(ActiveDocument);
        StartupProject = Convert(StartupProject);
        Breakpoints = Breakpoints.ToDictionary(kv => Convert(kv.Key)!, kv => kv.Value);
        ExpandedNodes = ExpandedNodes.Select(n => Convert(n)!).ToList();
        SelectedNode = Convert(SelectedNode);
        return this;
    }
}
