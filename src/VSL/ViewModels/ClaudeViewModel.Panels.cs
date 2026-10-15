using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;

namespace VSL.ViewModels;

/// <summary>The Modes menu (with the effort slider), Account &amp; Usage, and MCP servers.</summary>
public sealed partial class ClaudeViewModel
{
    static readonly (string Value, string Label, string Description, string Icon)[] AllModes =
    [
        ("default", "Manual", "Claude asks before each edit or command.", "Hand"),
        ("acceptEdits", "Edit automatically", "Claude edits files without asking; commands still need your approval.", "CodeBrackets"),
        ("plan", "Plan", "Claude explores the code and proposes a plan before changing anything.", "PlanDoc"),
        ("auto", "Auto", "Claude runs what passes a safety check and stops to ask about anything risky.", "Bolt"),
    ];

    static readonly string[] ScopeOrder = ["local", "project", "user", "claudeai", "enterprise", "dynamic"];

    public const string UsagePageUrl = "https://claude.ai/settings/usage";
    public const string McpDocsUrl = "https://code.claude.com/docs/en/mcp";

    JsonElement _account;
    List<McpServerItem> _mcpServers = [];
    DispatcherTimer? _mcpPoll;

    // ---- Modes and effort -----------------------------------------------------------------------------------

    public ObservableCollection<ModeOption> Modes { get; } = [];

    public ObservableCollection<EffortStep> EffortSteps { get; } = [];

    [ObservableProperty]
    public partial bool IsModesOpen { get; set; }

    /// <summary>The reasoning effort for this session (low ... max), when the model supports it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortLabel))]
    public partial string? Effort { get; set; }

    public string EffortLabel => $"Effort ({EffortStep.LabelOf(Effort ?? "high")})";

    public bool HasEffort => EffortSteps.Count > 0;

    void RefreshModes()
    {
        var withAuto = SelectedModel?.SupportsAutoMode == true || PermissionMode == "auto";
        Modes.Clear();
        foreach (var (value, label, description, icon) in AllModes.Where(m => m.Value != "auto" || withAuto))
            Modes.Add(new ModeOption(value, label, description, icon) { IsSelected = value == PermissionMode });
    }

    void RefreshEffortSteps()
    {
        EffortSteps.Clear();
        if (SelectedModel?.SupportsEffort == true)
            foreach (var level in SelectedModel.EffortLevels)
                EffortSteps.Add(new EffortStep(level, EffortStep.LabelOf(level)));
        MarkEffort();
        OnPropertyChanged(nameof(HasEffort));
    }

    void MarkEffort()
    {
        var index = EffortSteps.ToList().FindIndex(s => s.Value == Effort);
        for (var i = 0; i < EffortSteps.Count; i++)
        {
            EffortSteps[i].IsSelected = i == index;
            EffortSteps[i].IsReached = i <= index;
        }
    }

    partial void OnEffortChanged(string? value) => MarkEffort();

    [RelayCommand]
    void ToggleModes() => IsModesOpen = !IsModesOpen;

    [RelayCommand]
    void SelectMode(ModeOption? option)
    {
        if (option is not null)
            PermissionMode = option.Value;
        IsModesOpen = false;
    }

    [RelayCommand]
    public async Task SetEffortAsync(EffortStep? step)
    {
        if (step is null)
            return;
        Effort = step.Value;
        _settings.ClaudeEffort = step.Value;
        _settings.Save();
        if (_session is { HasExited: false } session)
            await SendControlAsync(() => session.ApplyFlagSettingsAsync(new { effortLevel = step.Value }));
    }

    /// <summary>A new CLI starts with the remembered effort, or reports the one its settings give.</summary>
    async Task ApplySessionSettingsAsync(ClaudeSession session)
    {
        try
        {
            if (_settings.ClaudeEffort is { Length: > 0 } effort && SelectedModel?.SupportsEffort == true)
            {
                await session.ApplyFlagSettingsAsync(new { effortLevel = effort });
                Effort = effort;
            }
            else
            {
                var settings = await session.GetSettingsAsync();
                if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("applied", out var applied)
                    && ClaudeJson.Str(applied, "effort") is { Length: > 0 } current)
                    Effort = current;
            }
        }
        catch (InvalidOperationException)
        {
            // An older CLI without these requests: the slider shows the default.
        }
    }

    // ---- Account & Usage -----------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsUsageOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsagePeriodShown))]
    public partial UsageInfo? Usage { get; set; }

    [ObservableProperty]
    public partial bool IsUsageLoading { get; set; }

    [ObservableProperty]
    public partial string? UsageError { get; set; }

    /// <summary>Day (false) or week (true) for "What's contributing to your limits usage?".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsagePeriodShown))]
    public partial bool ShowUsageWeek { get; set; }

    public UsagePeriod? UsagePeriodShown => ShowUsageWeek ? Usage?.Week : Usage?.Day;

    [RelayCommand]
    public async Task OpenUsageAsync()
    {
        IsMcpOpen = false;
        IsModesOpen = false;
        IsUsageOpen = true;
        IsUsageLoading = true;
        UsageError = null;
        try
        {
            var session = await EnsureSessionAsync();
            Usage = ParseUsage(await session.GetUsageAsync(), _account, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            UsageError = "Usage is not available: " + ex.Message;
        }
        finally
        {
            IsUsageLoading = false;
        }
    }

    [RelayCommand]
    void ShowUsagePeriod(string? period) => ShowUsageWeek = period == "week";

    [RelayCommand]
    void OpenUrl(string? url)
    {
        if (url is { Length: > 0 })
            OpenUrlRequested?.Invoke(url);
    }

    [RelayCommand]
    void CloseDialogs()
    {
        IsUsageOpen = false;
        IsMcpOpen = false;
        IsModesOpen = false;
        McpDraft = null;
    }

    /// <summary>The get_usage response as the dialog shows it.</summary>
    internal static UsageInfo ParseUsage(JsonElement usage, JsonElement account, DateTimeOffset now)
    {
        var rows = new List<UsageRow>();
        var plan = ClaudeJson.Str(account, "subscriptionType")
            ?? (ClaudeJson.Str(usage, "subscription_type") is { Length: > 0 } type ? "Claude " + char.ToUpperInvariant(type[0]) + type[1..] : null);
        rows.Add(new UsageRow("Auth method", plan is not null ? "Claude.ai account" : "API key"));
        if (ClaudeJson.Str(account, "email") is { Length: > 0 } email)
            rows.Add(new UsageRow("Email", email));
        if (ClaudeJson.Str(account, "organization") is { Length: > 0 } organization)
            rows.Add(new UsageRow("Organization", organization));
        if (plan is not null)
            rows.Add(new UsageRow("Plan", plan));

        var limits = new List<UsageLimit>();
        if (usage.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            if (rateLimits.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in list.EnumerateArray())
                {
                    var kind = ClaudeJson.Str(limit, "kind") ?? "";
                    var model = limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                        && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? ClaudeJson.Str(m, "display_name") : null;
                    var label = kind switch
                    {
                        "session" => "Session (5hr)",
                        "weekly_all" => "Weekly (7 day)",
                        "weekly_scoped" when model is not null => $"Weekly {model}",
                        _ => kind.Replace('_', ' '),
                    };
                    var percent = limit.TryGetProperty("percent", out var p) && p.TryGetDouble(out var value) ? value : 0;
                    limits.Add(new UsageLimit(label, percent, ResetText(ClaudeJson.Str(limit, "resets_at"), now),
                        percent >= 80 || ClaudeJson.Str(limit, "severity") is { } severity && severity != "normal"));
                }
            }
            else
            {
                foreach (var (key, label) in new[] { ("five_hour", "Session (5hr)"), ("seven_day", "Weekly (7 day)") })
                    if (rateLimits.TryGetProperty(key, out var window) && window.ValueKind == JsonValueKind.Object
                        && window.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var percent))
                        limits.Add(new UsageLimit(label, percent, ResetText(ClaudeJson.Str(window, "resets_at"), now), percent >= 80));
            }
        }

        UsagePeriod? Period(string key, string span)
        {
            if (!usage.TryGetProperty("behaviors", out var behaviors) || !behaviors.TryGetProperty(key, out var period) || period.ValueKind != JsonValueKind.Object)
                return null;
            int Count(string name) => period.TryGetProperty(name, out var c) && c.TryGetInt32(out var n) ? n : 0;
            var insights = new List<UsageInsight>();
            if (period.TryGetProperty("behaviors", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in list.EnumerateArray())
                {
                    var pct = b.TryGetProperty("pct", out var v) && v.TryGetDouble(out var d) ? Math.Round(d) : 0;
                    insights.Add(ClaudeJson.Str(b, "key") switch
                    {
                        "long_context" => new UsageInsight($"{pct}% of your usage was at more than 150k tokens of context",
                            "Long conversations cost more even when cached: /compact during a task, /clear when you switch tasks."),
                        "cron" => new UsageInsight($"{pct}% of your usage came from sessions active for 8+ hours",
                            "Often background or loop sessions; make sure the continuous use is intended."),
                        var other => new UsageInsight($"{pct}% of your usage: {other?.Replace('_', ' ')}", null),
                    });
                }
            }
            var skills = period.TryGetProperty("skills", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => new UsageShare("/" + ClaudeJson.Str(x, "name"), $"{(x.TryGetProperty("pct", out var sp) && sp.TryGetDouble(out var sv) ? Math.Round(sv) : 0)}%")).ToList()
                : [];
            return new UsagePeriod($"{span} · {Count("request_count")} requests · {Count("session_count")} sessions", insights, skills);
        }

        return new UsageInfo { Account = rows, Limits = limits, Day = Period("day", "Last 24h"), Week = Period("week", "Last 7d") };
    }

    internal static string ResetText(string? iso, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(iso, out var at))
            return "";
        var left = at - now;
        return left.TotalMinutes < 1 ? "Resets soon"
            : left.TotalHours < 1 ? $"Resets in {(int)left.TotalMinutes}m"
            : left.TotalHours < 24 ? $"Resets in {(int)Math.Round(left.TotalHours)}h"
            : left.TotalDays < 7 ? $"Resets in {(int)Math.Round(left.TotalDays)}d"
            : $"Resets {at.ToLocalTime():MMM d}";
    }

    // ---- MCP servers ---------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsMcpOpen { get; set; }

    public ObservableCollection<McpGroup> McpGroups { get; } = [];

    [ObservableProperty]
    public partial string McpFilter { get; set; } = "";

    [ObservableProperty]
    public partial string? McpMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddingMcpServer))]
    public partial McpServerDraft? McpDraft { get; set; }

    public bool IsAddingMcpServer => McpDraft is not null;

    public IReadOnlyList<McpScopeChoice> McpScopes => McpScopeChoice.All;

    partial void OnMcpFilterChanged(string value) => GroupMcpServers();

    partial void OnIsMcpOpenChanged(bool value)
    {
        if (!value)
            _mcpPoll?.Stop();
    }

    [RelayCommand]
    public async Task OpenMcpAsync()
    {
        IsUsageOpen = false;
        IsModesOpen = false;
        IsMcpOpen = true;
        McpMessage = null;
        await RefreshMcpAsync();
    }

    /// <summary>Reads the servers' state; while any is still connecting (or signing in), checks again.</summary>
    public async Task RefreshMcpAsync()
    {
        try
        {
            var session = await EnsureSessionAsync();
            var status = await session.McpStatusAsync();
            _mcpServers = status.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Array
                ? servers.EnumerateArray().Select(s => new McpServerItem(ClaudeJson.Str(s, "name") ?? "", ClaudeJson.Str(s, "status") ?? "",
                    ClaudeJson.Str(s, "scope") ?? ClaudeJson.Str(s, "source") ?? "",
                    s.TryGetProperty("config", out var config) ? ClaudeJson.Str(config, "type") : null)).ToList()
                : [];
            GroupMcpServers();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            McpMessage = "MCP servers are not available: " + ex.Message;
        }
        if (IsMcpOpen && _mcpServers.Any(s => s.IsPending || s.NeedsAuth))
        {
            _mcpPoll ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
            {
                _mcpPoll!.Stop();
                await RefreshMcpAsync();
            });
            _mcpPoll.Start();
        }
    }

    void GroupMcpServers()
    {
        McpGroups.Clear();
        var filtered = _mcpServers.Where(s => McpFilter.Length == 0 || s.Name.Contains(McpFilter, StringComparison.OrdinalIgnoreCase));
        foreach (var group in filtered.GroupBy(s => s.Scope).OrderBy(g => Array.IndexOf(ScopeOrder, g.Key) is var i and >= 0 ? i : ScopeOrder.Length))
        {
            var title = group.Key switch
            {
                "local" => "Local",
                "project" => "Project",
                "user" => "User",
                "claudeai" => "claude.ai",
                "enterprise" => "Enterprise",
                "dynamic" => "Session",
                var other => other,
            };
            McpGroups.Add(new McpGroup($"{title} ({group.Count()})", group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        }
    }

    [RelayCommand]
    async Task AuthenticateMcp(McpServerItem? server)
    {
        if (server is null || _session is not { } session)
            return;
        try
        {
            var result = await session.McpAuthenticateAsync(server.Name);
            if (ClaudeJson.Str(result, "authUrl") is { Length: > 0 } url)
            {
                OpenUrlRequested?.Invoke(url);
                McpMessage = $"Finish signing in to {server.Name} in your browser.";
            }
        }
        catch (InvalidOperationException ex)
        {
            McpMessage = ex.Message;
        }
        await RefreshMcpAsync();
    }

    [RelayCommand]
    async Task ReconnectMcp(McpServerItem? server)
    {
        if (server is null || _session is not { } session)
            return;
        McpMessage = $"Reconnecting {server.Name}...";
        try
        {
            await session.McpReconnectAsync(server.Name);
            McpMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            McpMessage = ex.Message;
        }
        await RefreshMcpAsync();
    }

    [RelayCommand]
    async Task ToggleMcp(McpServerItem? server)
    {
        if (server is null || _session is not { } session)
            return;
        try
        {
            await session.McpToggleAsync(server.Name, enabled: server.IsDisabled);
        }
        catch (InvalidOperationException ex)
        {
            McpMessage = ex.Message;
        }
        await RefreshMcpAsync();
    }

    [RelayCommand]
    void StartAddMcpServer() => McpDraft = new McpServerDraft();

    [RelayCommand]
    void CancelAddMcpServer() => McpDraft = null;

    /// <summary>`claude mcp add`, then a fresh CLI (when idle) so the new server connects.</summary>
    [RelayCommand]
    public async Task AddMcpServerAsync()
    {
        if (McpDraft is not { } draft || CliPath is null)
            return;
        draft.Errors.Clear();
        var name = draft.Name.Trim();
        var target = draft.Target.Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            draft.Errors.Add("Enter a name without spaces.");
        if (target.Length == 0)
            draft.Errors.Add(draft.Transport == "stdio" ? "Enter the command that starts the server." : "Enter the server's URL.");
        if (draft.Errors.Count > 0)
            return;
        List<string> args = ["mcp", "add", "--scope", draft.Scope.Value, "--transport", draft.Transport, name];
        if (draft.Transport == "stdio")
            args.AddRange(["--", .. SplitCommandLine(target)]);
        else
            args.Add(target);
        var (exit, output) = await RunCliAsync(args);
        if (exit != 0)
        {
            draft.Errors.Add(output.Trim().Length > 0 ? output.Trim() : $"claude mcp add exited with code {exit}.");
            return;
        }
        McpDraft = null;
        McpMessage = $"Added {name}.";
        if (!IsRunning)
            await StopSessionAsync();
        await RefreshMcpAsync();
    }

    async Task<(int Exit, string Output)> RunCliAsync(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(CliPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _workingDirectory(),
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment.Remove("CLAUDECODE");
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>Splits a command line on spaces, keeping quoted parts together.</summary>
    internal static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var c in line)
        {
            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any)
                    parts.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0 || any)
            parts.Add(current.ToString());
        return parts;
    }

    /// <summary>/usage, /mcp and /effort open VSL's own dialogs, as in the VS Code extension.</summary>
    async Task<bool> HandleLocalCommandAsync(string text)
    {
        var name = text.Split(' ', 2)[0];
        var argument = text.Length > name.Length ? text[name.Length..].Trim() : "";
        switch (name)
        {
            case "/usage" or "/cost" or "/status":
                await OpenUsageAsync();
                return true;
            case "/mcp":
                await OpenMcpAsync();
                return true;
            case "/effort" when argument.Length == 0:
                IsModesOpen = true;
                return true;
            case "/effort" when EffortSteps.FirstOrDefault(s => s.Value == argument) is { } step:
                await SetEffortAsync(step);
                Items.Add(new NoticeChatItem($"Effort set to {step.Label}.", isError: false));
                return true;
            default:
                return false;
        }
    }
}
