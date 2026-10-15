using System.IO.Compression;
using System.Text.Json;
using VSL.Build;

namespace VSL.Templates;

/// <summary>
/// The SDK's project templates: the template engine's cache (what <c>dotnet new list</c> shows, including each
/// template's CLI option names) plus the template's ide.host.json, which says what Visual Studio puts on its
/// "Additional information" page and how it labels it.
/// </summary>
public static class TemplateCatalog
{
    // Visual Studio lists the everyday templates first; everything else follows by name.
    static readonly string[] PreferredOrder =
        ["console", "blazor", "webapp", "webapi", "mvc", "web", "classlib", "razorclasslib", "worker", "grpc", "blazorwasm", "xunit", "nunit", "mstest"];

    static readonly string[] LanguageOrder = ["C#", "F#", "VB"];

    // Plumbing parameters, not choices a person makes when creating a project.
    static readonly HashSet<string> PlumbingSymbols = new(StringComparer.Ordinal)
    {
        "name", "language", "type", "skipRestore", "ExcludeLaunchSettings", "UserSecretsId", "langVersion",
        "TargetFrameworkOverride", "HostIdentifier", "kestrelHttpPort", "kestrelHttpsPort", "iisHttpPort", "iisHttpsPort",
    };

    static readonly Dictionary<string, string> AuthLabels = new(StringComparer.Ordinal)
    {
        ["None"] = "None",
        ["Individual"] = "Individual Accounts",
        ["IndividualB2C"] = "Individual Accounts (Azure AD B2C)",
        ["SingleOrg"] = "Microsoft identity platform",
        ["MultiOrg"] = "Microsoft identity platform (multiple organizations)",
        ["Windows"] = "Windows",
    };

    public static string IconCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSL", "template-icons");

    public static async Task<IReadOnlyList<ProjectTemplate>> LoadAsync(CancellationToken ct = default)
    {
        // Listing refreshes the cache when template packages changed (and creates it on first use).
        await RunDotnetAsync(["new", "list", "--type", "project"], ct);
        var version = (await RunDotnetAsync(["--version"], ct)).Trim().Split('\n')[^1].Trim();
        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } h
            ? h
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Path.Combine(home, ".templateengine", "dotnetcli", version, "templatecache.json");
        if (!File.Exists(cache))
            throw new InvalidOperationException($"The .NET template cache was not found ({cache}).");
        var json = await File.ReadAllTextAsync(cache, ct);
        return await Task.Run(() => Parse(json, IconCacheDirectory), ct);
    }

    /// <summary>Reads templatecache.json; <paramref name="iconDirectory"/> receives extracted template icons.</summary>
    internal static IReadOnlyList<ProjectTemplate> Parse(string json, string? iconDirectory)
    {
        using var doc = JsonDocument.Parse(json);
        using var packages = new PackageReader();
        var found = new List<(ProjectTemplate Template, int Precedence)>();
        foreach (var t in doc.RootElement.GetProperty("TemplateInfo").EnumerateArray())
        {
            var tags = t.TryGetProperty("TagsCollection", out var tc) && tc.ValueKind == JsonValueKind.Object ? tc : default;
            if (Str(tags, "type") != "project")
                continue;
            var identity = Str(t, "Identity") ?? "";
            var name = Str(t, "Name") ?? identity;
            var shortName = t.TryGetProperty("ShortNameList", out var sn) && sn.ValueKind == JsonValueKind.Array && sn.GetArrayLength() > 0
                ? sn[0].GetString() ?? ""
                : "";
            if (shortName.Length == 0)
                continue;

            var mountPoint = Str(t, "MountPointUri") ?? "";
            if (mountPoint.StartsWith("file:", StringComparison.Ordinal))
                mountPoint = new Uri(mountPoint).LocalPath;
            var configDir = Path.GetDirectoryName((Str(t, "ConfigPlace") ?? "").Replace('\\', '/'))?.Replace('\\', '/') ?? "";
            var ide = packages.ReadJson(mountPoint, configDir + "/ide.host.json") ?? packages.ReadJson(mountPoint, configDir + "/vs-2017.3.host.json");
            using var hostDoc = Str(t, "HostData") is { Length: > 0 } hostJson ? JsonDocument.Parse(hostJson) : null;
            var cliSymbols = hostDoc?.RootElement.TryGetProperty("symbolInfo", out var si) == true ? si : default;

            var classifications = t.TryGetProperty("Classifications", out var cl) && cl.ValueKind == JsonValueKind.Array
                ? cl.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToList()
                : [];
            var platforms = new List<string>();
            if (ide?.RootElement.TryGetProperty("tags", out var ideTags) == true && ideTags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in ideTags.EnumerateArray())
                {
                    var add = Strings(tag, "add");
                    var remove = Strings(tag, "remove");
                    if (Str(tag, "type") == "platform")
                        platforms.AddRange(add.Where(p => !platforms.Contains(p)));
                    else if (Str(tag, "type") == "projectType")
                        classifications = [.. classifications.Where(c => !remove.Contains(c)), .. add.Where(a => !classifications.Contains(a))];
                }
            }

            var options = Options(t, ide?.RootElement, cliSymbols);
            var defaultName = Str(t, "DefaultName") is { Length: > 0 } dn ? dn : new string(name.Where(char.IsLetterOrDigit).ToArray());
            var preferred = Array.IndexOf(PreferredOrder, shortName);
            var ideOrder = ide?.RootElement.TryGetProperty("order", out var o) == true && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 5000;
            var template = new ProjectTemplate(identity, Str(t, "GroupIdentity") ?? identity, name, shortName, Str(t, "Description") ?? "",
                Str(t, "Author"), Str(tags, "language"), classifications, platforms, preferred >= 0 ? preferred : 100 + ideOrder, defaultName, options)
            {
                IconPath = iconDirectory is null ? null
                    : ide?.RootElement.TryGetProperty("icon", out var icon) == true && icon.GetString() is { Length: > 0 } iconPath
                        ? packages.Extract(mountPoint, configDir + "/" + iconPath, Path.Combine(iconDirectory, Sanitize(identity) + Path.GetExtension(iconPath)))
                        : null,
            };
            found.Add((template, t.TryGetProperty("Precedence", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0));
        }

        // Several versions of a template can be installed (e.g. net9 and net10 MAUI): keep the preferred one.
        return found
            .GroupBy(x => (x.Template.GroupIdentity, x.Template.Language))
            .Select(g => g.MaxBy(x => x.Precedence).Template)
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => Array.IndexOf(LanguageOrder, x.Language) is var i and >= 0 ? i : LanguageOrder.Length)
            .ToList();
    }

    /// <summary>What VS shows on "Additional information": framework, authentication, HTTPS, then the template's visible symbols.</summary>
    static List<TemplateOption> Options(JsonElement template, JsonElement? ide, JsonElement cliSymbols)
    {
        var parameters = template.TryGetProperty("Parameters", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray().ToDictionary(p => Str(p, "Name") ?? "", StringComparer.Ordinal)
            : [];
        var ideSymbols = ide?.TryGetProperty("symbolInfo", out var s) == true && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(x => Str(x, "id") is not null).ToList()
            : [];
        var httpsSymbol = ide is { } i && Str(i, "disableHttpsSymbol") is { } h ? h : null;

        var options = new List<TemplateOption>();
        void Add(string symbol, Func<JsonElement, string, TemplateOption?> make)
        {
            if (options.Any(o => o.Symbol == symbol) || !parameters.TryGetValue(symbol, out var p))
                return;
            var cli = cliSymbols.ValueKind == JsonValueKind.Object && cliSymbols.TryGetProperty(symbol, out var info) ? info : default;
            if (Bool(cli, "isHidden"))
                return;
            var cliName = "--" + (Str(cli, "longName") is { Length: > 0 } longName ? longName : symbol);
            if (make(p, cliName) is { } option)
                options.Add(option);
        }

        Add("Framework", (p, cli) => Choice(p, "Framework", cli, "Framework", FrameworkLabel));
        Add("auth", (p, cli) => Choice(p, "auth", cli, "Authentication type", v => AuthLabels.GetValueOrDefault(v, v)));
        if (httpsSymbol is not null)
            Add(httpsSymbol, (p, cli) => Checkbox(p, httpsSymbol, cli, "Configure for HTTPS", null, invert: true));

        if (ide is not null)
        {
            foreach (var info in ideSymbols.Where(x => Bool(x, "isVisible")))
            {
                var symbol = Str(info, "id")!;
                var label = info.TryGetProperty("name", out var n) ? Str(n, "text") : null;
                var description = info.TryGetProperty("description", out var d) ? Str(d, "text") : null;
                Add(symbol, (p, cli) => DataType(p) == "choice"
                    ? Choice(p, symbol, cli, label ?? DisplayName(p, symbol), null, description)
                    : DataType(p) == "bool" ? Checkbox(p, symbol, cli, label ?? DisplayName(p, symbol), description, Bool(info, "invertBoolean")) : null);
            }
        }
        else
        {
            // Without IDE metadata, offer the template's own non-plumbing switches, as `dotnet new --help` does.
            foreach (var (symbol, p) in parameters.Where(x => !PlumbingSymbols.Contains(x.Key)))
                Add(symbol, (_, cli) => DataType(p) == "choice" ? Choice(p, symbol, cli, DisplayName(p, symbol), null)
                    : DataType(p) == "bool" ? Checkbox(p, symbol, cli, DisplayName(p, symbol), null, invert: false) : null);
        }
        return options;
    }

    static TemplateOption? Choice(JsonElement p, string symbol, string cli, string label, Func<string, string>? choiceLabel, string? description = null)
    {
        if (!p.TryGetProperty("Choices", out var choices) || choices.ValueKind != JsonValueKind.Object)
            return null;
        var list = choices.EnumerateObject().Select(c => new TemplateChoice(c.Name,
            choiceLabel?.Invoke(c.Name) ?? (Str(c.Value, "DisplayName") is { Length: > 0 } dn ? Clean(dn) : c.Name),
            Str(c.Value, "Description") is { Length: > 0 } desc ? desc : null)).ToList();
        return list.Count == 0 ? null
            : new TemplateOption(symbol, cli, Clean(label), description ?? Str(p, "Description"), TemplateOptionKind.Choice, list, Str(p, "DefaultValue"));
    }

    static TemplateOption Checkbox(JsonElement p, string symbol, string cli, string label, string? description, bool invert) =>
        new(symbol, cli, Clean(label), description ?? Str(p, "Description"), TemplateOptionKind.Bool, [], Str(p, "DefaultValue") ?? "false", invert);

    static string DataType(JsonElement p) => Str(p, "DataType") ?? "";

    static string DisplayName(JsonElement p, string symbol) => Str(p, "DisplayName") is { Length: > 0 } dn ? dn : Words(symbol);

    /// <summary>"net10.0" → ".NET 10.0 (Long Term Support)", as in VS's framework box.</summary>
    internal static string FrameworkLabel(string tfm)
    {
        if (tfm.StartsWith("netstandard", StringComparison.Ordinal))
            return ".NET Standard " + tfm["netstandard".Length..];
        if (tfm.StartsWith("net", StringComparison.Ordinal) && Version.TryParse(tfm[3..], out var v) && v.Major >= 5)
            return $".NET {v.Major}.{v.Minor} ({(v.Major % 2 == 0 ? "Long Term Support" : "Standard Term Support")})";
        return tfm;
    }

    /// <summary>Removes access-key underscores ("Do not use _top-level statements").</summary>
    static string Clean(string label) => label.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_");

    /// <summary>"UseProgramMain" → "Use program main".</summary>
    static string Words(string symbol)
    {
        var chars = new List<char>();
        for (var i = 0; i < symbol.Length; i++)
        {
            if (i > 0 && char.IsUpper(symbol[i]) && !char.IsUpper(symbol[i - 1]))
                chars.Add(' ');
            chars.Add(i == 0 ? char.ToUpperInvariant(symbol[i]) : chars.Count > 0 && chars[^1] == ' ' ? char.ToLowerInvariant(symbol[i]) : symbol[i]);
        }
        return new string(chars.ToArray());
    }

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    static List<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    static async Task<string> RunDotnetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var exit = await runner.WaitAsync(ct);
        lock (lines)
        {
            var output = string.Join('\n', lines);
            return exit == 0 ? output : throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed:\n{output}");
        }
    }

    /// <summary>Reads files from template packages (.nupkg archives or folders), opening each archive once.</summary>
    sealed class PackageReader : IDisposable
    {
        readonly Dictionary<string, ZipArchive?> _archives = new(StringComparer.Ordinal);

        public JsonDocument? ReadJson(string mountPoint, string path)
        {
            using var stream = Open(mountPoint, path);
            if (stream is null)
                return null;
            try
            {
                return JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string? Extract(string mountPoint, string path, string destination)
        {
            if (File.Exists(destination))
                return destination;
            using var stream = Open(mountPoint, path);
            if (stream is null)
                return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var file = File.Create(destination);
                stream.CopyTo(file);
                return destination;
            }
            catch (IOException)
            {
                return null;
            }
        }

        Stream? Open(string mountPoint, string path)
        {
            path = path.TrimStart('/');
            if (Directory.Exists(mountPoint))
            {
                var file = Path.Combine(mountPoint, path);
                return File.Exists(file) ? File.OpenRead(file) : null;
            }
            if (!_archives.TryGetValue(mountPoint, out var zip))
            {
                try
                {
                    zip = File.Exists(mountPoint) ? ZipFile.OpenRead(mountPoint) : null;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    zip = null;
                }
                _archives[mountPoint] = zip;
            }
            var entry = zip?.GetEntry(path);
            if (entry is null)
                return null;
            // Copy out: callers dispose the stream while the archive stays open for the next template.
            var memory = new MemoryStream();
            using (var s = entry.Open())
                s.CopyTo(memory);
            memory.Position = 0;
            return memory;
        }

        public void Dispose()
        {
            foreach (var zip in _archives.Values)
                zip?.Dispose();
        }
    }
}
