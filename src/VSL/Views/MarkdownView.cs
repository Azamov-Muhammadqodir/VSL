using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
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
public sealed partial class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>Handles clicked links (inherited, so a panel sets it once for every answer inside it).</summary>
    public static readonly AttachedProperty<System.Windows.Input.ICommand?> LinkCommandProperty =
        AvaloniaProperty.RegisterAttached<MarkdownView, Control, System.Windows.Input.ICommand?>("LinkCommand", inherits: true);

    public static System.Windows.Input.ICommand? GetLinkCommand(Control control) => control.GetValue(LinkCommandProperty);

    public static void SetLinkCommand(Control control, System.Windows.Input.ICommand? value) => control.SetValue(LinkCommandProperty, value);

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
        var links = new List<MdLink>();
        block.Inlines = new InlineCollection();
        foreach (var inline in Inlines(text, links))
            block.Inlines.Add(inline);
        if (links.Count > 0)
            MakeLinksClickable(block, links);
        return block;
    }

    /// <summary>
    /// Links are runs, which cannot take clicks, so the block hit-tests the pointer against the link ranges:
    /// a hand over a link, and a click (not a selection drag) opens it.
    /// </summary>
    static void MakeLinksClickable(SelectableTextBlock block, IReadOnlyList<MdLink> links)
    {
        block.Tag = links;
        var normal = block.Cursor;
        block.PointerMoved += (_, e) => block.Cursor = LinkAt(block, e.GetPosition(block)) is null ? normal : HandCursor;
        block.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd)
                return;
            if (LinkAt(block, e.GetPosition(block)) is { } link)
            {
                Activate(block, link.Target);
                e.Handled = true;
            }
        };
    }

    static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>The link under a point of a rendered block, if any.</summary>
    public static MdLink? LinkAt(SelectableTextBlock block, Point point)
    {
        if (block.Tag is not IReadOnlyList<MdLink> links)
            return null;
        var hit = block.TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
            return null;
        var index = hit.TextPosition;
        return links.FirstOrDefault(l => index >= l.Start && index < l.End);
    }

    static void Activate(Control source, string target)
    {
        if (source.GetValue(LinkCommandProperty) is { } command && command.CanExecute(target))
        {
            command.Execute(target);
            return;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            _ = TopLevel.GetTopLevel(source)?.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>
    /// Inline Markdown: `code`, **bold**, *italic* / _italic_, [text](url), bare URLs. Links (including inline code
    /// that names a source file, like `src/App.cs:42`) are recorded with their character range in the block's text.
    /// </summary>
    internal static List<Inline> Inlines(string text, List<MdLink> links)
    {
        var offset = 0;
        return Build(text);

        List<Inline> Build(string part)
        {
            var result = new List<Inline>();
            var plain = new System.Text.StringBuilder();
            void Add(Inline inline, string runText, string? link)
            {
                if (link is not null)
                    links.Add(new MdLink(offset, offset + runText.Length, link));
                offset += runText.Length;
                result.Add(inline);
            }
            void Flush()
            {
                if (plain.Length == 0)
                    return;
                var chunk = plain.ToString();
                plain.Clear();
                var last = 0;
                foreach (System.Text.RegularExpressions.Match url in BareUrl().Matches(chunk))
                {
                    if (url.Index > last)
                        Add(new Run(chunk[last..url.Index]), chunk[last..url.Index], null);
                    var address = url.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                    Add(LinkRun(address), address, address);
                    last = url.Index + address.Length;
                }
                if (last < chunk.Length)
                    Add(new Run(chunk[last..]), chunk[last..], null);
            }
            for (var i = 0; i < part.Length; i++)
            {
                var c = part[i];
                if (c == '`' && part.IndexOf('`', i + 1) is var close and > 0)
                {
                    Flush();
                    var code = part[(i + 1)..close];
                    var isFile = FileReference().IsMatch(code);
                    Add(new Run(code)
                    {
                        FontFamily = CodeFont,
                        Background = InlineCodeBackground,
                        Foreground = isFile ? LinkBrush : InlineCodeForeground,
                        FontSize = 12,
                    }, code, isFile ? code : null);
                    i = close;
                    continue;
                }
                if (c == '*' && i + 1 < part.Length && part[i + 1] == '*' && part.IndexOf("**", i + 2, StringComparison.Ordinal) is var boldEnd and > 0)
                {
                    Flush();
                    var bold = new Span { FontWeight = FontWeight.SemiBold };
                    foreach (var inner in Build(part[(i + 2)..boldEnd]))
                        bold.Inlines.Add(inner);
                    result.Add(bold);
                    i = boldEnd + 1;
                    continue;
                }
                if (c is '*' or '_' && i + 1 < part.Length && !char.IsWhiteSpace(part[i + 1]) && (i == 0 || !char.IsLetterOrDigit(part[i - 1]))
                    && part.IndexOf(c, i + 1) is var italicEnd and > 0 && !char.IsWhiteSpace(part[italicEnd - 1])
                    && (italicEnd + 1 >= part.Length || !char.IsLetterOrDigit(part[italicEnd + 1])))
                {
                    Flush();
                    var italic = part[(i + 1)..italicEnd];
                    Add(new Run(italic) { FontStyle = FontStyle.Italic }, italic, null);
                    i = italicEnd;
                    continue;
                }
                if (c == '[' && part.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0 && part.IndexOf(')', mid) is var end and > 0
                    && !part[(i + 1)..mid].Contains('\n'))
                {
                    Flush();
                    var label = part[(i + 1)..mid];
                    Add(LinkRun(label), label, part[(mid + 2)..end].Trim());
                    i = end;
                    continue;
                }
                plain.Append(c);
            }
            Flush();
            return result;
        }
    }

    static Run LinkRun(string text) => new(text) { Foreground = LinkBrush, TextDecorations = TextDecorations.Underline };

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>()\[\]`]+")]
    private static partial System.Text.RegularExpressions.Regex BareUrl();

    /// <summary>Inline code that names a source file, optionally with a line: Program.cs, src/A.cs:42, A.razor#L3-L9.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[\w.\-/~]*\.(cs|razor|cshtml|csproj|fsproj|vbproj|sln|slnx|props|targets|json|xml|xaml|axaml|config|md|txt|js|ts|tsx|jsx|css|scss|html|htm|yml|yaml|toml|ini|py|sh|sql|proto|fs|vb|ps1|editorconfig|gitignore)((:|#L)\d+((-L?|:)\d+)?)?$")]
    private static partial System.Text.RegularExpressions.Regex FileReference();

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
