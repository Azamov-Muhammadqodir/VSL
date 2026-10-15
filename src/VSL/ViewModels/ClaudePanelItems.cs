using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VSL.ViewModels;

/// <summary>A permission mode in the Modes menu.</summary>
public sealed partial class ModeOption(string value, string label, string description, string icon) : ObservableObject
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    public string Description { get; } = description;

    public string Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A step of the effort slider (low ... max).</summary>
public sealed partial class EffortStep(string value, string label) : ObservableObject
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    /// <summary>The chosen step.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>At or below the chosen step (drawn filled).</summary>
    [ObservableProperty]
    public partial bool IsReached { get; set; }

    public static string LabelOf(string value) => value switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        "auto" => "Auto",
        _ => value,
    };
}

public sealed record UsageRow(string Label, string Value);

/// <summary>One plan limit: a bar with its use and when it resets.</summary>
public sealed record UsageLimit(string Label, double Percent, string ResetText, bool IsHigh)
{
    public string PercentText => $"{Math.Round(Percent)}%";
}

public sealed record UsageInsight(string Text, string? Hint)
{
    public bool HasHint => Hint is { Length: > 0 };
}

public sealed record UsageShare(string Name, string Percent);

/// <summary>"What's contributing to your limits usage?" for a day or a week.</summary>
public sealed record UsagePeriod(string Summary, IReadOnlyList<UsageInsight> Insights, IReadOnlyList<UsageShare> Skills)
{
    public bool HasSkills => Skills.Count > 0;

    public bool HasInsights => Insights.Count > 0;
}

/// <summary>The Account &amp; Usage dialog.</summary>
public sealed class UsageInfo
{
    public IReadOnlyList<UsageRow> Account { get; init; } = [];

    public IReadOnlyList<UsageLimit> Limits { get; init; } = [];

    public UsagePeriod? Day { get; init; }

    public UsagePeriod? Week { get; init; }

    public bool HasLimits => Limits.Count > 0;

    public bool HasBehaviors => Day is not null || Week is not null;
}

/// <summary>A configured MCP server and its connection state.</summary>
public sealed record McpServerItem(string Name, string Status, string Scope, string? Transport)
{
    public string StatusLabel => Status switch
    {
        "connected" => "Connected",
        "needs-auth" => "Needs Auth",
        "pending" => "Connecting",
        "failed" => "Failed",
        "disabled" => "Disabled",
        _ => Status,
    };

    public bool IsConnected => Status == "connected";

    public bool NeedsAuth => Status == "needs-auth";

    public bool IsFailed => Status == "failed";

    public bool IsPending => Status == "pending";

    public bool IsDisabled => Status == "disabled";

    public string ToggleLabel => IsDisabled ? "Enable" : "Disable";
}

public sealed record McpGroup(string Title, IReadOnlyList<McpServerItem> Servers);

/// <summary>Where `claude mcp add` stores a server.</summary>
public sealed record McpScopeChoice(string Value, string Label)
{
    public override string ToString() => Label;

    public static readonly IReadOnlyList<McpScopeChoice> All =
    [
        new("local", "Local (this project, only you)"),
        new("project", "Project (.mcp.json, shared)"),
        new("user", "User (all your projects)"),
    ];
}

public sealed partial class McpServerDraft : ObservableObject
{
    public static IReadOnlyList<string> Transports { get; } = ["stdio", "http", "sse"];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetHint))]
    public partial string Transport { get; set; } = "stdio";

    /// <summary>The command line (stdio) or URL (http, sse).</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = "";

    [ObservableProperty]
    public partial McpScopeChoice Scope { get; set; } = McpScopeChoice.All[0];

    public string TargetHint => Transport == "stdio" ? "Command, e.g. npx -y @modelcontextprotocol/server-everything" : "URL, e.g. https://example.com/mcp";

    public ObservableCollection<string> Errors { get; } = [];
}
