using System.Text.Json;

namespace VSL.Services;

/// <summary>The Claude Code panel's settings (model, mode, effort, account), kept in %LOCALAPPDATA%\VSL\ClaudeCode.VisualStudio.json.</summary>
public sealed class Settings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string? ClaudeModel { get; set; }

    public string? ClaudePermissionMode { get; set; }

    public string? ClaudeEffort { get; set; }

    public string? ClaudeAccount { get; set; }

    /// <summary>The panel's language: "en" or "uz" (null: Uzbek on an Uzbek Windows, else English).</summary>
    public string? UiLanguage { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSL", "ClaudeCode.VisualStudio.json");

    public static Settings Load()
    {
        try
        {
            // TrimStart: a file saved by an editor may start with a byte order mark, which the reader rejects.
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath).TrimStart('\uFEFF')) ?? new Settings() : new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
