using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using VSL.Lsp;

namespace VSL.Editor;

/// <summary>One IntelliSense entry backed by an LSP completion item.</summary>
public sealed class CompletionData(LspCompletionItem item, Action<LspCompletionItem, int> committed) : ICompletionData
{
    Control? _content;
    TextBlock? _description;

    public LspCompletionItem Item { get; private set; } = item;

    public IImage? Image => null;

    public string Text => Item.FilterText ?? Item.Label;

    public object Content => _content ??= BuildContent();

    public object Description => _description ??= new TextBlock
    {
        Text = Item.Detail ?? Item.Label,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 480,
    };

    public double Priority => 0;

    public void SetDocumentation(LspCompletionItem resolved, string? documentation)
    {
        Item = resolved;
        var text = string.Join("\n\n", new[] { resolved.Detail, documentation }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Length > 0)
            ((TextBlock)Description).Text = text;
    }

    Control BuildContent() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            new Views.VsIcon { Kind = KindIcon(Item.Kind) },
            new TextBlock { Text = Item.Label, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    /// <summary>Visual Studio's IntelliSense glyph for an LSP CompletionItemKind.</summary>
    public static string KindIcon(int kind) => kind switch
    {
        2 or 3 or 4 => "Method",      // Method, Function, Constructor
        5 => "Field",
        6 => "Local",                 // Variable
        7 => "Class",
        8 => "Interface",
        9 => "Namespace",             // Module
        10 => "Property",
        13 => "Enum",
        14 => "Keyword",
        15 => "Snippet",
        20 => "EnumMember",
        21 => "Constant",
        22 => "Struct",
        23 => "Event",
        24 => "Operator",
        25 => "TypeParameter",
        _ => "Keyword",
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var text = Item.TextEdit?.NewText ?? Item.InsertText ?? Item.Label;
        var (clean, caret) = Item.IsSnippet ? Snippet.Expand(text) : (text, -1);
        var start = completionSegment.Offset;
        textArea.Document.Replace(start, completionSegment.Length, clean);
        if (caret >= 0)
            textArea.Caret.Offset = start + caret;
        committed(Item, start);
    }
}

/// <summary>Minimal LSP snippet support: placeholders become their default text, the caret goes to $0 or $1.</summary>
public static partial class Snippet
{
    // A backslash escapes "$" in LSP snippets, so "\$5" is literal text, not tab stop 5.
    [GeneratedRegex(@"(?<!\\)\$\{(\d+)(?::((?:[^{}\\]|\\.)*))?\}|(?<!\\)\$(\d+)")]
    private static partial Regex Placeholder();

    public static (string Text, int Caret) Expand(string snippet)
    {
        var sb = new StringBuilder();
        int caret = -1, firstTab = -1, last = 0;
        foreach (Match m in Placeholder().Matches(snippet))
        {
            sb.Append(Unescape(snippet[last..m.Index]));
            var index = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            if (index == "0")
                caret = sb.Length;
            else if (firstTab < 0)
                firstTab = sb.Length;
            sb.Append(m.Groups[2].Success ? Unescape(m.Groups[2].Value) : "");
            last = m.Index + m.Length;
        }
        sb.Append(Unescape(snippet[last..]));
        return (sb.ToString(), firstTab >= 0 ? firstTab : caret);
    }

    static string Unescape(string s) => s.Replace(@"\$", "$").Replace(@"\}", "}").Replace(@"\\", @"\");
}

/// <summary>Feeds LSP signature help into AvaloniaEdit's overload insight window.</summary>
public sealed class SignatureProvider : IOverloadProvider
{
    readonly LspSignatureHelp _help;
    int _selected;

    public SignatureProvider(LspSignatureHelp help)
    {
        _help = help;
        _selected = help.ActiveSignature;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, 0, Count - 1);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentIndexText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
        }
    }

    public int Count => _help.Signatures.Count;

    public string? CurrentIndexText => Count > 1 ? $"{_selected + 1} of {Count}" : null;

    public object CurrentHeader
    {
        get
        {
            var sig = _help.Signatures[_selected];
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
            var active = _help.ActiveParameter < sig.Parameters.Count ? sig.Parameters[_help.ActiveParameter] : null;
            var at = active is null ? -1 : sig.Label.IndexOf(active, StringComparison.Ordinal);
            if (at < 0)
            {
                block.Text = sig.Label;
                return block;
            }
            block.Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run(sig.Label[..at]),
                new Avalonia.Controls.Documents.Run(active!) { FontWeight = FontWeight.Bold },
                new Avalonia.Controls.Documents.Run(sig.Label[(at + active!.Length)..]),
            };
            return block;
        }
    }

    public object? CurrentContent => _help.Signatures[_selected].Documentation;
}
