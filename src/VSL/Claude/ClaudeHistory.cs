using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSL.Claude;

public sealed record ClaudeConversation(string SessionId, string Title, DateTime Modified, string FilePath);

/// <summary>Past conversations of a folder, read from the transcripts Claude Code saves for each session.</summary>
public static partial class ClaudeHistory
{
    /// <summary>A prompt without the editor context VSL appends to it.</summary>
    public static string StripContext(string text) => ContextPattern().Replace(text, "").Trim();

    [GeneratedRegex(@"<ide_context>.*?</ide_context>", RegexOptions.Singleline)]
    private static partial Regex ContextPattern();

    /// <summary>Newest first; each titled by its summary or first prompt.</summary>
    public static IReadOnlyList<ClaudeConversation> List(string workingDirectory, int max = 50, string? configDirectory = null)
    {
        var dir = ClaudeCli.ProjectDirectory(workingDirectory, configDirectory);
        if (!Directory.Exists(dir))
            return [];
        var list = new List<ClaudeConversation>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(max))
        {
            if (Title(file.FullName) is { } title)
                list.Add(new ClaudeConversation(Path.GetFileNameWithoutExtension(file.Name), title, file.LastWriteTime, file.FullName));
        }
        return list;
    }

    /// <summary>The conversation's events in order, for showing a resumed conversation.</summary>
    public static IReadOnlyList<ClaudeEvent> Load(string transcriptPath)
    {
        var events = new List<ClaudeEvent>();
        foreach (var line in ReadLines(transcriptPath))
        {
            using var doc = TryParse(line);
            if (doc is null || IsSideChainOrMeta(doc.RootElement))
                continue;
            foreach (var ev in ClaudeProtocol.Parse(doc.RootElement))
            {
                if (ev is ClaudeUserText user && !IsPrompt(user.Text))
                    continue;
                events.Add(ev);
            }
        }
        return events;
    }

    static string? Title(string path)
    {
        string? firstPrompt = null;
        foreach (var line in ReadLines(path).Take(400))
        {
            using var doc = TryParse(line);
            if (doc is null)
                continue;
            var root = doc.RootElement;
            var type = ClaudeProtocol.Str(root, "type");
            if (type is "summary" or "custom-title" or "ai-title"
                && (ClaudeProtocol.Str(root, "customTitle") ?? ClaudeProtocol.Str(root, "summary") ?? ClaudeProtocol.Str(root, "title")) is { Length: > 0 } named)
                return named;
            if (firstPrompt is null && type == "user" && !IsSideChainOrMeta(root))
                foreach (var ev in ClaudeProtocol.Parse(root))
                    if (ev is ClaudeUserText user && IsPrompt(user.Text))
                        firstPrompt = OneLine(StripContext(user.Text));
        }
        return firstPrompt;
    }

    /// <summary>Command echoes, caveats and reminders are stored as user turns but are not something the user typed.</summary>
    static bool IsPrompt(string text) => text.Trim().Length > 0 && !text.TrimStart().StartsWith('<');

    static bool IsSideChainOrMeta(JsonElement root) =>
        root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True
        || root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;

    static string OneLine(string text)
    {
        var line = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return line.Length > 120 ? line[..117] + "..." : line;
    }

    static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadLines(path).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    static JsonDocument? TryParse(string line)
    {
        if (!line.StartsWith('{'))
            return null;
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
