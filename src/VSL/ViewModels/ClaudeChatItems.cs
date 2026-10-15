using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VSL.Claude;

namespace VSL.ViewModels;

/// <summary>A row in the Claude Code conversation.</summary>
public abstract class ChatItem : ObservableObject;

public sealed class UserChatItem(string text, string? context, IReadOnlyList<ClaudeAttachment>? images = null) : ChatItem
{
    public string Text { get; } = text;

    public bool HasText => Text.Length > 0;

    public IReadOnlyList<ClaudeAttachment> Images { get; } = images ?? [];

    public bool HasImages => Images.Count > 0;

    /// <summary>The editor context sent with the prompt ("Program.cs:10-12"), if any.</summary>
    public string? Context { get; } = context;

    public bool HasContext => Context is not null;
}

/// <summary>Assistant text (Markdown); grows while it streams.</summary>
public sealed partial class TextChatItem(string text) : ChatItem
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    internal string? StreamKey { get; set; }
}

public sealed partial class ThinkingChatItem(string text) : ChatItem
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    internal string? StreamKey { get; set; }
}

public enum ToolState
{
    Running,
    WaitingForApproval,
    Done,
    Error,
}

public sealed record TodoEntry(string Content, string Status)
{
    public bool IsDone => Status == "completed";

    public bool IsActive => Status == "in_progress";
}

/// <summary>A tool call, shown the way Claude Code shows it: "Update(Program.cs)", then its result.</summary>
public sealed partial class ToolChatItem : ChatItem
{
    public ToolChatItem(string toolUseId, string name, string title, string? target, string? filePath)
    {
        ToolUseId = toolUseId;
        Name = name;
        Title = title;
        Target = target;
        FilePath = filePath;
    }

    public string ToolUseId { get; }

    public string Name { get; }

    /// <summary>"Read", "Update", "Bash", "Search"...</summary>
    public string Title { get; }

    /// <summary>The file, command or pattern it acts on.</summary>
    public string? Target { get; }

    /// <summary>The file it reads or changes (clickable).</summary>
    public string? FilePath { get; }

    public bool HasTarget => Target is { Length: > 0 };

    public string? Code { get; init; }

    public bool HasCode => Code is { Length: > 0 };

    public IReadOnlyList<DiffLine> Diff { get; init; } = [];

    public bool HasDiff => Diff.Count > 0;

    /// <summary>While its approval card is open the card shows the diff, so the row does not repeat it.</summary>
    public bool ShowDiff => HasDiff && State != ToolState.WaitingForApproval;

    /// <summary>The user said no on the approval card.</summary>
    public bool WasDenied { get; set; }

    public IReadOnlyList<TodoEntry> Todos { get; init; } = [];

    public bool HasTodos => Todos.Count > 0;

    /// <summary>For Agent / Task: the subagent's own tool calls, nested under it.</summary>
    public ObservableCollection<ToolChatItem> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>The last few steps while collapsed (as Claude Code shows a running subagent), all when expanded.</summary>
    public IEnumerable<ToolChatItem> VisibleChildren => IsExpanded ? Children : Children.TakeLast(3);

    public string? MoreChildrenText => !IsExpanded && Children.Count > 3 ? $"+{Children.Count - 3} more tool uses" : null;

    public bool HasMoreChildren => MoreChildrenText is not null;

    public void AddChild(ToolChatItem child)
    {
        Children.Add(child);
        if (IsRunning)
            Summary = $"{Children.Count} tool use{(Children.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(HasChildren));
        OnChildrenShown();
    }

    void OnChildrenShown()
    {
        OnPropertyChanged(nameof(VisibleChildren));
        OnPropertyChanged(nameof(MoreChildrenText));
        OnPropertyChanged(nameof(HasMoreChildren));
    }

    partial void OnIsExpandedChanged(bool value) => OnChildrenShown();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsError), nameof(IsDone), nameof(ShowDiff))]
    public partial ToolState State { get; set; }

    public bool IsRunning => State is ToolState.Running or ToolState.WaitingForApproval;

    public bool IsError => State == ToolState.Error;

    public bool IsDone => State == ToolState.Done;

    /// <summary>The one-line outcome under the call ("Read 42 lines", the first output line...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string? Summary { get; set; }

    public bool HasSummary => Summary is { Length: > 0 };

    /// <summary>The full result text, shown when expanded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreResult))]
    public partial string? Result { get; set; }

    public bool HasMoreResult => Result is { Length: > 0 } r && r.Trim() != Summary?.Trim();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>The "Allow Claude to...?" card for a tool that needs approval.</summary>
public sealed partial class PermissionChatItem : ChatItem
{
    public PermissionChatItem(ClaudePermissionRequest request, string question, string? sessionLabel)
    {
        Request = request;
        Question = question;
        SessionLabel = sessionLabel;
    }

    public ClaudePermissionRequest Request { get; }

    public string Question { get; }

    public string? Description { get; init; }

    public bool HasDescription => Description is { Length: > 0 };

    public string? Code { get; init; }

    public bool HasCode => Code is { Length: > 0 };

    public IReadOnlyList<DiffLine> Diff { get; init; } = [];

    public bool HasDiff => Diff.Count > 0;

    /// <summary>For edits: the file and its proposed content, to open as a side-by-side diff.</summary>
    public (string Path, string Before, string After)? Proposal { get; init; }

    public bool CanOpenDiff => Proposal is not null;

    /// <summary>"Yes, allow all edits during this session" when the CLI suggests a rule or mode.</summary>
    public string? SessionLabel { get; }

    public bool HasSessionOption => SessionLabel is not null;

    [ObservableProperty]
    public partial string Feedback { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial bool IsResolved { get; set; }

    public bool IsPending => !IsResolved;

    [ObservableProperty]
    public partial string? Outcome { get; set; }
}

public sealed partial class QuestionOption(string label, string? description) : ObservableObject
{
    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool HasDescription => Description is { Length: > 0 };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class QuestionEntry : ObservableObject
{
    public QuestionEntry(string question, string? header, bool multiSelect, IEnumerable<QuestionOption> options)
    {
        Question = question;
        Header = header;
        MultiSelect = multiSelect;
        Options = new ObservableCollection<QuestionOption>(options);
        foreach (var option in Options)
            option.PropertyChanged += (_, e) =>
            {
                // Single choice: picking one clears the others.
                if (e.PropertyName == nameof(QuestionOption.IsSelected) && option.IsSelected && !MultiSelect)
                    foreach (var other in Options.Where(o => o != option))
                        other.IsSelected = false;
            };
    }

    public string Question { get; }

    public string? Header { get; }

    public bool HasHeader => Header is { Length: > 0 };

    public bool MultiSelect { get; }

    public ObservableCollection<QuestionOption> Options { get; }

    /// <summary>A typed answer instead of the options ("Other").</summary>
    [ObservableProperty]
    public partial string Other { get; set; } = "";

    public string? Answer
    {
        get
        {
            if (Other.Trim().Length > 0)
                return Other.Trim();
            var picked = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            return picked.Count == 0 ? null : string.Join(", ", picked);
        }
    }
}

/// <summary>AskUserQuestion: Claude's clarifying questions with their options.</summary>
public sealed partial class QuestionChatItem(ClaudePermissionRequest request, IReadOnlyList<QuestionEntry> questions) : ChatItem
{
    public ClaudePermissionRequest Request { get; } = request;

    public IReadOnlyList<QuestionEntry> Questions { get; } = questions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial bool IsResolved { get; set; }

    public bool IsPending => !IsResolved;

    [ObservableProperty]
    public partial string? Outcome { get; set; }
}

/// <summary>ExitPlanMode: the plan Claude wants to carry out.</summary>
public sealed partial class PlanChatItem(ClaudePermissionRequest request, string plan) : ChatItem
{
    public ClaudePermissionRequest Request { get; } = request;

    public string Plan { get; } = plan;

    [ObservableProperty]
    public partial string Feedback { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial bool IsResolved { get; set; }

    public bool IsPending => !IsResolved;

    [ObservableProperty]
    public partial string? Outcome { get; set; }
}

/// <summary>An image (or other file) attached to a prompt.</summary>
public sealed class ClaudeAttachment(string name, string mediaType, byte[] data)
{
    /// <summary>The API's per-image limit.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    Bitmap? _thumbnail;
    bool _thumbnailLoaded;

    public string Name { get; } = name;

    public string MediaType { get; } = mediaType;

    public byte[] Data { get; } = data;

    public string SizeText => Data.Length >= 1024 * 1024 ? $"{Data.Length / 1024.0 / 1024:0.0} MB" : $"{Math.Max(1, Data.Length / 1024)} KB";

    public ClaudeImage ToImage() => new(MediaType, Convert.ToBase64String(Data));

    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnailLoaded)
                return _thumbnail;
            _thumbnailLoaded = true;
            try
            {
                using var stream = new MemoryStream(Data);
                _thumbnail = Bitmap.DecodeToWidth(stream, 160);
            }
            catch (Exception)
            {
                // Formats the decoder does not read (e.g. some WebP files) are sent without a preview.
                _thumbnail = null;
            }
            return _thumbnail;
        }
    }

    /// <summary>The image type Claude accepts, from the file's signature; null for anything else.</summary>
    public static string? ImageMediaType(ReadOnlySpan<byte> data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };
}

public sealed class NoticeChatItem(string text, bool isError) : ChatItem
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;
}

public sealed record ClaudeModelChoice(
    string Value,
    string Label,
    string? Description,
    bool SupportsEffort = false,
    IReadOnlyList<string>? SupportedEffortLevels = null,
    bool SupportsAutoMode = false)
{
    public IReadOnlyList<string> EffortLevels => SupportedEffortLevels ?? [];

    /// <summary>The label without its note ("Default (recommended)" → "Default"), for the compact picker.</summary>
    public string ShortLabel => Label.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? Label[..i] : Label;

    public bool HasDescription => Description is { Length: > 0 };

    public override string ToString() => Label;
}

public sealed record ClaudeCommand(string Name, string Description, string? ArgumentHint)
{
    public string Display => "/" + Name;
}

/// <summary>A "/" command or "@" file offered while typing.</summary>
public sealed record ClaudeSuggestion(string Insert, string Label, string? Detail, bool IsFile);

/// <summary>What the editor shows right now, sent with a prompt like the VS Code extension does.</summary>
public sealed record ClaudeEditorContext(string FilePath, int? StartLine, int? EndLine, string? SelectedText)
{
    public string Label => StartLine is { } s && EndLine is { } e
        ? $"{Path.GetFileName(FilePath)}:{s}{(e != s ? "-" + e : "")}"
        : Path.GetFileName(FilePath);
}

internal static class ClaudeJson
{
    public static string? Str(JsonElement e, string name) => ClaudeProtocol.Str(e, name);

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
