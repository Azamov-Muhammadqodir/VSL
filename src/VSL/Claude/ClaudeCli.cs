using System.Text.RegularExpressions;

namespace VSL.Claude;

/// <summary>Finds the Claude Code CLI and where it keeps its per-project session transcripts.</summary>
public static partial class ClaudeCli
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The `claude` executable: VSL_CLAUDE_PATH, then PATH, the native installer's ~/.local/bin, the older
    /// ~/.claude/local install, and finally the copy bundled with the VS Code extension.
    /// </summary>
    public static string? FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("VSL_CLAUDE_PATH") is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "claude"))
            .Concat([Path.Combine(Home, ".local", "bin", "claude"), Path.Combine(Home, ".claude", "local", "claude")]);
        if (candidates.FirstOrDefault(File.Exists) is { } found)
            return found;
        var extensions = Path.Combine(Home, ".vscode", "extensions");
        if (!Directory.Exists(extensions))
            return null;
        return Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(dir => Path.Combine(dir, "resources", "native-binary", "claude"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Claude Code's configuration folder (CLAUDE_CONFIG_DIR or ~/.claude).</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude");

    /// <summary>The folder holding the session transcripts (*.jsonl) of conversations started in <paramref name="workingDirectory"/>.</summary>
    public static string ProjectDirectory(string workingDirectory) =>
        Path.Combine(ConfigDirectory, "projects", NonAlphanumeric().Replace(Path.GetFullPath(workingDirectory).TrimEnd('/'), "-"));

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
