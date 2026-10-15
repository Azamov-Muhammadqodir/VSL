using System.Text.Json;

namespace VSL.Claude;

/// <summary>
/// A Claude Code account: a configuration folder of its own, as in <c>CLAUDE_CONFIG_DIR=~/.claude-work claude</c>.
/// ~/.claude is the default one; each ~/.claude-NAME folder is another.
/// </summary>
public sealed record ClaudeAccount(string Name, string ConfigDirectory, string? Email, bool IsSignedIn)
{
    /// <summary>The default folder: started without CLAUDE_CONFIG_DIR, its settings sit in ~/.claude.json.</summary>
    public bool IsDefault => SamePath(ConfigDirectory, ClaudeAccounts.DefaultDirectory);

    public string Detail => Email is { Length: > 0 } ? Email : IsSignedIn ? ConfigDirectory : "Not signed in";

    /// <summary>Points a CLI process at this account (the default one runs without CLAUDE_CONFIG_DIR).</summary>
    public void Apply(IDictionary<string, string?> environment)
    {
        if (IsDefault)
            environment.Remove("CLAUDE_CONFIG_DIR");
        else
            environment["CLAUDE_CONFIG_DIR"] = ConfigDirectory;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public override string ToString() => Name;
}

/// <summary>Finds the accounts on this machine and adds new ones.</summary>
public static class ClaudeAccounts
{
    const string Prefix = ".claude-";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DefaultDirectory => Path.Combine(Home, ".claude");

    /// <summary>The default account, every ~/.claude-NAME folder, and CLAUDE_CONFIG_DIR if it points elsewhere.</summary>
    public static IReadOnlyList<ClaudeAccount> Discover()
    {
        var accounts = new List<ClaudeAccount> { Read("Default", DefaultDirectory) };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Home, Prefix + "*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                accounts.Add(Read(Path.GetFileName(dir).Substring(Prefix.Length), dir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            && !accounts.Any(a => ClaudeAccount.SamePath(a.ConfigDirectory, configured)))
            accounts.Add(Read(Path.GetFileName(configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), configured));
        return accounts;
    }

    /// <summary>The account the CLI uses when nothing is chosen: CLAUDE_CONFIG_DIR, else the default.</summary>
    public static ClaudeAccount? Current(IReadOnlyList<ClaudeAccount> accounts) =>
        accounts.FirstOrDefault(a => ClaudeAccount.SamePath(a.ConfigDirectory, ClaudeCli.ConfigDirectory)) ?? accounts.FirstOrDefault();

    /// <summary>A folder for a new account (~/.claude-NAME); sign in to it once with the CLI.</summary>
    public static ClaudeAccount Create(string name)
    {
        var clean = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (clean.Length == 0)
            throw new ArgumentException("Enter a name with letters or digits.", nameof(name));
        var dir = Path.Combine(Home, Prefix + clean);
        Directory.CreateDirectory(dir);
        return Read(clean, dir);
    }

    static ClaudeAccount Read(string name, string dir)
    {
        // The CLI keeps the signed-in account in .claude.json: inside the folder, or ~/.claude.json for the default one.
        var settings = Path.Combine(dir, ".claude.json");
        if (!File.Exists(settings) && ClaudeAccount.SamePath(dir, DefaultDirectory))
            settings = Path.Combine(Home, ".claude.json");
        string? email = null;
        try
        {
            if (File.Exists(settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("oauthAccount", out var account))
                    email = ClaudeProtocol.Str(account, "emailAddress");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        var signedIn = email is not null || File.Exists(Path.Combine(dir, ".credentials.json"));
        return new ClaudeAccount(name, dir, email, signedIn);
    }
}
