using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VSL.Views;

/// <summary>
/// Renders Claude's Markdown: headings, paragraphs, lists, block quotes, fenced code (with a copy button),
/// tables, rules, and inline bold / italic / code / links. Text stays selectable. Re-renders are throttled so a
/// streaming answer does not rebuild on every token.
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    static readonly FontFamily CodeFont = new("avares://VSL/Assets/Fonts#Cascadia Mono, DejaVu Sans Mono, monospace");
    static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#1A1A1A"));
    static readonly IBrush InlineCodeBackground = new SolidColorBrush(Color.Parse("#33FFFFFF"));
    static readonly IBrush InlineCodeForeground = new SolidColorBrush(Color.Parse("#E8B887"));
    static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#4DAAFC"));
    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#A0A0A0"));
    static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#3C3C3C"));

    DispatcherTimer? _throttle;
    string? _rendered;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownProperty)
            return;
        // The first render is immediate; later ones (streaming) are coalesced.
        if (_rendered is null || Child is null)
        {
            Render();
            return;
        }
        _throttle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) =>
        {
            _throttle!.Stop();
            Render();
        });
        _throttle.Stop();
        _throttle.Start();
    }

    void Render()
    {
        var text = Markdown ?? "";
        if (text == _rendered && Child is not null)
            return;
        _rendered = text;
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in MarkdownParser.Parse(text))
            panel.Children.Add(Build(block));
        Child = panel;
    }

    Control Build(MdBlock block) => block switch
    {
        MdHeading h => Paragraph(h.Text, h.Level switch { 1 => 18, 2 => 16, _ => 14 }, FontWeight.SemiBold, new Thickness(0, h.Level <= 2 ? 6 : 2, 0, 0)),
        MdParagraph p => Paragraph(p.Text),
        MdCode c => CodeBlock(c),
        MdList l => List(l),
        MdQuote q => new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Child = Paragraph(q.Text, foreground: MutedBrush),
        },
        MdTable t => Table(t),
        _ => new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 4) },
    };

    static SelectableTextBlock Paragraph(string text, double size = 13, FontWeight? weight = null, Thickness margin = default, IBrush? foreground = null)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = margin, LineHeight = size * 1.45 };
        if (weight is { } w)
            block.FontWeight = w;
        if (foreground is not null)
            block.Foreground = foreground;
        block.Inlines = new InlineCollection();
        foreach (var inline in Inlines(text))
            block.Inlines.Add(inline);
        return block;
    }

    /// <summary>`code`, **bold**, *italic* / _italic_, [text](url).</summary>
    internal static IEnumerable<Inline> Inlines(string text)
    {
        var plain = new System.Text.StringBuilder();
        IEnumerable<Inline> Flush()
        {
            if (plain.Length > 0)
            {
                yield return new Run(plain.ToString());
                plain.Clear();
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`' && text.IndexOf('`', i + 1) is var close and > 0)
            {
                foreach (var run in Flush())
                    yield return run;
                yield return new Run(text[(i + 1)..close]) { FontFamily = CodeFont, Background = InlineCodeBackground, Foreground = InlineCodeForeground, FontSize = 12 };
                i = close;
                continue;
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var boldEnd and > 0)
            {
                foreach (var run in Flush())
                    yield return run;
                var bold = new Span { FontWeight = FontWeight.SemiBold };
                foreach (var inner in Inlines(text[(i + 2)..boldEnd]))
                    bold.Inlines.Add(inner);
                yield return bold;
                i = boldEnd + 1;
                continue;
            }
            if (c is '*' or '_' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                && text.IndexOf(c, i + 1) is var italicEnd and > 0 && !char.IsWhiteSpace(text[italicEnd - 1])
                && (italicEnd + 1 >= text.Length || !char.IsLetterOrDigit(text[italicEnd + 1])))
            {
                foreach (var run in Flush())
                    yield return run;
                yield return new Run(text[(i + 1)..italicEnd]) { FontStyle = FontStyle.Italic };
                i = italicEnd;
                continue;
            }
            if (c == '[' && text.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0 && text.IndexOf(')', mid) is var end and > 0
                && !text[(i + 1)..mid].Contains('\n'))
            {
                foreach (var run in Flush())
                    yield return run;
                yield return new Run(text[(i + 1)..mid]) { Foreground = LinkBrush, TextDecorations = TextDecorations.Underline };
                i = end;
                continue;
            }
            plain.Append(c);
        }
        foreach (var run in Flush())
            yield return run;
    }

    Control CodeBlock(MdCode code)
    {
        var copy = new Button
        {
            Classes = { "tbi" },
            Content = new VsIcon { Kind = "Copy" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Opacity = 0.75,
        };
        ToolTip.SetTip(copy, "Copy");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(code.Text);
        };
        var body = new SelectableTextBlock
        {
            Text = code.Text,
            FontFamily = CodeFont,
            FontSize = 12,
            Margin = new Thickness(10, code.Language.Length > 0 ? 22 : 8, 10, 8),
        };
        var grid = new Grid { Children = { new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = body }, copy } };
        if (code.Language.Length > 0)
            grid.Children.Add(new TextBlock { Text = code.Language, FontSize = 11, Foreground = MutedBrush, Margin = new Thickness(10, 5, 0, 0) });
        return new Border { Background = CodeBackground, CornerRadius = new CornerRadius(5), BorderBrush = RuleBrush, BorderThickness = new Thickness(1), Child = grid };
    }

    Control List(MdList list)
    {
        var panel = new StackPanel { Spacing = 3 };
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? $"{number++}." : item.Level % 2 == 0 ? "•" : "◦";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(item.Level * 18, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = marker, MinWidth = list.Ordered ? 20 : 14, Foreground = MutedBrush, FontSize = 13, LineHeight = 13 * 1.45 });
            var content = item.Checked is { } done
                ? new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { new CheckBox { IsChecked = done, IsHitTestVisible = false, MinHeight = 0, Padding = new Thickness(0) }, Paragraph(item.Text) },
                }
                : (Control)Paragraph(item.Text);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    Control Table(MdTable table)
    {
        var columns = table.Rows.Max(r => r.Count);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columns))) };
        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Background = r == 0 ? new SolidColorBrush(Color.Parse("#2A2A2A")) : null,
                    Child = Paragraph(table.Rows[r][c], 12, r == 0 ? FontWeight.SemiBold : null),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0), Child = grid, HorizontalAlignment = HorizontalAlignment.Left },
        };
    }
}

public abstract record MdBlock;

public sealed record MdHeading(int Level, string Text) : MdBlock;

public sealed record MdParagraph(string Text) : MdBlock;

public sealed record MdCode(string Language, string Text) : MdBlock;

public sealed record MdListItem(int Level, string Text, bool? Checked);

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

public sealed record MdQuote(string Text) : MdBlock;

public sealed record MdTable(IReadOnlyList<IReadOnlyList<string>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>A small block-level Markdown parser for chat answers (CommonMark subset).</summary>
public static class MarkdownParser
{
    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<MdBlock>();
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0)
                blocks.Add(new MdParagraph(string.Join(" ", paragraph.Select(l => l.Trim()))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                // An unterminated fence (still streaming) runs to the end.
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new MdCode(language, string.Join("\n", code)));
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (trimmed.StartsWith('#') && trimmed.TrimStart('#') is var heading && heading.StartsWith(' ') && trimmed.Length - heading.Length <= 6)
            {
                FlushParagraph();
                blocks.Add(new MdHeading(trimmed.Length - heading.Length, heading.Trim().TrimEnd('#').Trim()));
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new MdRule());
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quote.Add(lines[i].TrimStart()[1..].Trim());
                i--;
                blocks.Add(new MdQuote(string.Join(" ", quote)));
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>> { Cells(trimmed) };
                for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    rows.Add(Cells(lines[i].Trim()));
                i--;
                blocks.Add(new MdTable(rows));
                continue;
            }
            if (ListItem(line) is { } first)
            {
                FlushParagraph();
                var items = new List<MdListItem>();
                var ordered = first.Ordered;
                var start = first.Number;
                var indentBase = first.Indent;
                for (; i < lines.Length; i++)
                {
                    if (ListItem(lines[i]) is { } item && (item.Ordered == ordered || item.Indent > indentBase))
                    {
                        var itemText = item.Text;
                        bool? check = null;
                        if (itemText.StartsWith("[ ] ", StringComparison.Ordinal) || itemText.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                        {
                            check = itemText[1] != ' ';
                            itemText = itemText[4..];
                        }
                        items.Add(new MdListItem(Math.Max(0, (item.Indent - indentBase) / 2), itemText, check));
                    }
                    else if (lines[i].Trim().Length > 0 && lines[i].StartsWith("  ", StringComparison.Ordinal) && items.Count > 0)
                    {
                        // A continuation line of the previous item.
                        var last = items[^1];
                        items[^1] = last with { Text = last.Text + " " + lines[i].Trim() };
                    }
                    else
                    {
                        break;
                    }
                }
                i--;
                blocks.Add(new MdList(ordered, start, items));
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        return blocks;
    }

    sealed record ListMatch(int Indent, bool Ordered, int Number, string Text);

    static ListMatch? ListItem(string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var t = line.TrimStart();
        if (t.Length > 2 && t[0] is '-' or '*' or '+' && t[1] == ' ')
            return new ListMatch(indent, false, 1, t[2..].Trim());
        var digits = 0;
        while (digits < t.Length && char.IsDigit(t[digits]))
            digits++;
        if (digits is > 0 and < 4 && digits + 1 < t.Length && t[digits] is '.' or ')' && t[digits + 1] == ' ')
            return new ListMatch(indent, true, int.Parse(t[..digits]), t[(digits + 2)..].Trim());
        return null;
    }

    static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        return t.StartsWith('|') && t.Contains('-') && t.All(c => c is '|' or '-' or ':' or ' ');
    }

    static IReadOnlyList<string> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
}
