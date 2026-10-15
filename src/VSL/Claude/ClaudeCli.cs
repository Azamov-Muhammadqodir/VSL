using System.Text.RegularExpressions;

namespace VSL.Claude;

/// <summary>Finds the Claude Code CLI and where it keeps its per-project session transcripts.</summary>
public static partial class ClaudeCli
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The executable's file names: claude, or on Windows claude.exe (native installer) and claude.cmd (npm).</summary>
    static string[] ExecutableNames => Path.DirectorySeparatorChar == '\\' ? ["claude.exe", "claude.cmd"] : ["claude"];

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
            .Concat([Path.Combine(Home, ".local", "bin"), Path.Combine(Home, ".claude", "local")])
            .SelectMany(dir => ExecutableNames.Select(name => Path.Combine(dir.Trim().Trim('"'), name)));
        if (candidates.FirstOrDefault(File.Exists) is { } found)
            return found;
        var extensions = Path.Combine(Home, ".vscode", "extensions");
        if (!Directory.Exists(extensions))
            return null;
        return Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .SelectMany(dir => ExecutableNames.Select(name => Path.Combine(dir, "resources", "native-binary", name)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Claude Code's configuration folder (CLAUDE_CONFIG_DIR or ~/.claude).</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude");

    /// <summary>
    /// The folder holding the session transcripts (*.jsonl) of conversations started in <paramref name="workingDirectory"/>,
    /// in <paramref name="configDirectory"/> (an account's folder) or the current one.
    /// </summary>
    public static string ProjectDirectory(string workingDirectory, string? configDirectory = null) =>
        Path.Combine(configDirectory ?? ConfigDirectory, "projects", NonAlphanumeric().Replace(Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "-"));

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
