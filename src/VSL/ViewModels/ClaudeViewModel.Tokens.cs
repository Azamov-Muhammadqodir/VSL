using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;

namespace VSL.ViewModels;

/// <summary>
/// Tokens at a glance: how much context the conversation holds (with /context and /compact a click away), and how much
/// of the plan's session and weekly limits is used and when each starts over. The limits come with every answer
/// (rate_limit_event) and from get_usage when the CLI starts; neither costs tokens.
/// </summary>
public sealed partial class ClaudeViewModel
{
    static readonly TimeSpan LimitsRefresh = TimeSpan.FromMinutes(5);

    Avalonia.Threading.DispatcherTimer? _limitsTimer;
    DateTime _limitsFetched;

    /// <summary>The session and weekly limits, as the compact meter shows them.</summary>
    public ObservableCollection<UsageLimit> LiveLimits { get; } = [];

    public bool HasLiveLimits => LiveLimits.Count > 0;

    /// <summary>Context the conversation used at its last answer; null before the first answer or after /compact.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextTokensText), nameof(HasContextTokens))]
    public partial int? ContextTokens { get; set; }

    public bool HasContextTokens => ContextTokens is not null;

    public string ContextTokensText => ContextTokens is { } tokens ? FormatTokens(tokens) : "";

    /// <summary>/compact: Claude summarizes the conversation so far and continues from the summary (fewer tokens per answer).</summary>
    [RelayCommand(CanExecute = nameof(CanRunTokenCommand))]
    Task Compact() => RunCommandAsync("/compact");

    /// <summary>/context: what fills the context window, by category.</summary>
    [RelayCommand(CanExecute = nameof(CanRunTokenCommand))]
    Task ShowContext() => RunCommandAsync("/context");

    bool CanRunTokenCommand() => !IsRunning && CliPath is not null;

    Task RunCommandAsync(string command)
    {
        Prompt = command;
        return Send();
    }

    /// <summary>The limits from get_usage (no model call), when the CLI starts and every few minutes after.</summary>
    async Task RefreshLimitsAsync(ClaudeSession session)
    {
        try
        {
            var usage = ParseUsage(await session.GetUsageAsync(), _account, DateTimeOffset.Now);
            _limitsFetched = DateTime.Now;
            ShowLimits(usage.Limits);
        }
        catch (InvalidOperationException)
        {
            // An older CLI, or an API key account without plan limits.
        }
        EnsureLimitsTimer();
    }

    void ApplyRateLimits(ClaudeRateLimits limits)
    {
        var now = DateTimeOffset.Now;
        var shown = new List<UsageLimit>();
        foreach (var window in limits.Windows)
        {
            var label = window.Name switch
            {
                "five_hour" => "Session (5hr)",
                "seven_day" => "Weekly (7 day)",
                var other when other.StartsWith("seven_day_", StringComparison.Ordinal) => "Weekly " + Capitalize(other.Substring(10)),
                var other => other.Replace('_', ' '),
            };
            var percent = window.Utilization * 100;
            shown.Add(new UsageLimit(label, percent, window.ResetsAt is { } at ? ResetText(at, now) : "", percent >= 80, window.ResetsAt));
        }
        _limitsFetched = DateTime.Now;
        ShowLimits(shown);
        EnsureLimitsTimer();

        static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    void ShowLimits(IEnumerable<UsageLimit> limits)
    {
        LiveLimits.Clear();
        foreach (var limit in limits)
            LiveLimits.Add(limit);
        OnPropertyChanged(nameof(HasLiveLimits));
    }

    /// <summary>Keeps "resets in 2h" current, and asks for fresh limits now and then while the panel is idle.</summary>
    void EnsureLimitsTimer()
    {
        if (_limitsTimer is not null)
            return;
        _limitsTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(30), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var updated = LiveLimits.Select(l => l.ResetsAt is { } at ? l with { ResetText = ResetText(at, now) } : l).ToList();
            ShowLimits(updated);
            if (!IsRunning && DateTime.Now - _limitsFetched > LimitsRefresh && _session is { HasExited: false } session)
            {
                _limitsFetched = DateTime.Now;
                _ = RefreshLimitsAsync(session);
            }
        });
        _limitsTimer.Start();
    }
}
