using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using VSL.Views;

namespace VSL.VisualStudio.Views;

/// <summary>
/// Renders Claude's Markdown (VSL's <see cref="MarkdownParser"/>) as a read-only flow document: headings, paragraphs,
/// lists, block quotes, fenced code with a copy button, tables, rules, and inline bold / italic / code / links. Text
/// stays selectable and links are clickable. Re-renders are throttled so a streaming answer does not rebuild on
/// every token.
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(null, (d, _) => ((MarkdownView)d).OnMarkdownChanged()));

    /// <summary>Handles clicked links (inherited, so the panel sets it once for every answer inside it).</summary>
    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.RegisterAttached("LinkCommand", typeof(ICommand), typeof(MarkdownView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static ICommand? GetLinkCommand(DependencyObject element) => (ICommand?)element.GetValue(LinkCommandProperty);

    public static void SetLinkCommand(DependencyObject element, ICommand? value) => element.SetValue(LinkCommandProperty, value);

    internal static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    static readonly Brush CodeBackground = Frozen("#14000000");
    static readonly Brush InlineCodeBackground = Frozen("#22D97757");
    static readonly Brush InlineCodeForeground = Frozen("#E08A6D");
    static readonly Brush Accent = Frozen("#D97757");
    static readonly Brush CodeHeaderBackground = Frozen("#14808080");
    static readonly Brush RuleBrush = Frozen("#50808080");
    static readonly Brush HeaderBackground = Frozen("#1A808080");

    // VSL's MarkdownView patterns; file references here also take Windows paths and C/C++ files.
    static readonly Regex BareUrl = new(@"https?://[^\s<>()\[\]`]+", RegexOptions.Compiled);

    /// <summary>Inline code that names a source file, optionally with a line: Program.cs, src/A.cs:42, A.razor#L3-L9.</summary>
    static readonly Regex FileReference = new(
        @"^[\w.\-/\\~:]*\.(cs|razor|cshtml|csproj|fsproj|vbproj|sln|slnx|props|targets|json|xml|xaml|axaml|config|md|txt|js|ts|tsx|jsx|css|scss|html|htm|yml|yaml|toml|ini|py|sh|sql|proto|fs|vb|ps1|editorconfig|gitignore|cpp|h|hpp|c)((:|#L)\d+((-L?|:)\d+)?)?$",
        RegexOptions.Compiled);

    readonly RichTextBox _box;
    DispatcherTimer? _throttle;
    DispatcherTimer? _settle;
    string? _rendered;

    /// <summary>While the answer streams, a blinking caret follows the text; it goes once the text settles.</summary>
    bool _streaming;
    SolidColorBrush? _caret;

    public MarkdownView()
    {
        _box = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = true,
            IsTabStop = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        _box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Child = _box;
        // Copy buttons and the like follow the panel's language.
        System.ComponentModel.PropertyChangedEventManager.AddHandler(Loc.Instance, (_, _) =>
        {
            if (_rendered is not null)
                Render(force: true);
        }, nameof(Loc.Language));
    }

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    void OnMarkdownChanged()
    {
        // The first render is immediate; later ones (streaming) are coalesced.
        if (_rendered is null)
        {
            Render();
            return;
        }
        _streaming = true;
        _throttle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) =>
        {
            _throttle!.Stop();
            Render();
        }, Dispatcher);
        _throttle.Stop();
        _throttle.Start();
        _settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            _streaming = false;
            _caret?.BeginAnimation(Brush.OpacityProperty, null);
            Render(force: true);
        }, Dispatcher);
        _settle.Stop();
        _settle.Start();
    }

    void Render(bool force = false)
    {
        var text = Markdown ?? "";
        if (text == _rendered && !force)
            return;
        _rendered = text;
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 13, TextAlignment = TextAlignment.Left };
        // Links are underlined only under the pointer.
        var linkStyle = new Style(typeof(Hyperlink));
        linkStyle.Setters.Add(new Setter(Inline.TextDecorationsProperty, null));
        var hover = new Trigger { Property = ContentElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Inline.TextDecorationsProperty, TextDecorations.Underline));
        linkStyle.Triggers.Add(hover);
        document.Resources.Add(typeof(Hyperlink), linkStyle);
        document.SetResourceReference(FlowDocument.FontFamilyProperty, Microsoft.VisualStudio.Shell.VsFonts.EnvironmentFontFamilyKey);
        document.SetResourceReference(FlowDocument.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        foreach (var block in MarkdownParser.Parse(text))
            document.Blocks.Add(Build(block));
        if (document.Blocks.LastBlock is { } last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        if (_streaming && document.Blocks.LastBlock is Paragraph tail)
            tail.Inlines.Add(new Run(" \u258D") { Foreground = Caret() });
        _box.Document = document;
    }

    /// <summary>The streaming caret's brush, blinking.</summary>
    Brush Caret()
    {
        if (_caret is null)
        {
            _caret = new SolidColorBrush(((SolidColorBrush)Accent).Color);
            _caret.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(1, 0.15, TimeSpan.FromSeconds(0.55))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        return _caret;
    }

    Block Build(MdBlock block) => block switch
    {
        MdHeading h => Heading(h),
        MdParagraph p => Paragraph(p.Text),
        MdCode c => CodeBlock(c),
        MdList l => List(l),
        MdQuote q => Quote(q),
        MdTable t => Table(t),
        _ => new BlockUIContainer(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 4, 0, 4) }) { Margin = new Thickness(0, 0, 0, 8) },
    };

    Paragraph Paragraph(string text, double size = 13, FontWeight? weight = null, Thickness? margin = null)
    {
        var paragraph = new Paragraph { FontSize = size, Margin = margin ?? new Thickness(0, 0, 0, 10), LineHeight = size * 1.55 };
        if (weight is { } w)
            paragraph.FontWeight = w;
        paragraph.Inlines.AddRange(Inlines(text));
        return paragraph;
    }

    /// <summary>Headings: larger and bolder; the top two levels get a fine rule under them.</summary>
    Paragraph Heading(MdHeading heading)
    {
        var size = heading.Level switch { 1 => 19, 2 => 16.5, _ => 14.5 };
        var paragraph = Paragraph(heading.Text, size, heading.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            new Thickness(0, heading.Level <= 2 ? 8 : 4, 0, 8));
        if (heading.Level <= 2)
        {
            paragraph.BorderBrush = RuleBrush;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
        }
        return paragraph;
    }

    Block Quote(MdQuote quote)
    {
        var paragraph = Paragraph(quote.Text, margin: new Thickness(0));
        paragraph.FontStyle = FontStyles.Italic;
        paragraph.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return new Section(paragraph)
        {
            BorderBrush = Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>
    /// Inline Markdown: `code`, **bold**, *italic* / _italic_, [text](url), bare URLs. Links (including inline code
    /// that names a source file, like `src/App.cs:42`) become hyperlinks that go to the panel's link command.
    /// </summary>
    List<Inline> Inlines(string part)
    {
        var result = new List<Inline>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0)
                return;
            var chunk = plain.ToString();
            plain.Clear();
            var last = 0;
            foreach (Match url in BareUrl.Matches(chunk))
            {
                if (url.Index > last)
                    result.Add(new Run(chunk.Substring(last, url.Index - last)));
                var address = url.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                result.Add(Link(new Run(address), address));
                last = url.Index + address.Length;
            }
            if (last < chunk.Length)
                result.Add(new Run(chunk.Substring(last)));
        }
        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (c == '`' && part.IndexOf('`', i + 1) is var close and > 0)
            {
                Flush();
                var code = part.Substring(i + 1, close - i - 1);
                var run = new Run("\u2009" + code + "\u2009") { FontFamily = CodeFont, Background = InlineCodeBackground, Foreground = InlineCodeForeground, FontSize = 12 };
                result.Add(FileReference.IsMatch(code) ? Link(run, code) : run);
                i = close;
                continue;
            }
            if (c == '*' && i + 1 < part.Length && part[i + 1] == '*' && part.IndexOf("**", i + 2, StringComparison.Ordinal) is var boldEnd and > 0)
            {
                Flush();
                var bold = new Span { FontWeight = FontWeights.SemiBold };
                bold.Inlines.AddRange(Inlines(part.Substring(i + 2, boldEnd - i - 2)));
                result.Add(bold);
                i = boldEnd + 1;
                continue;
            }
            if (c is '*' or '_' && i + 1 < part.Length && !char.IsWhiteSpace(part[i + 1]) && (i == 0 || !char.IsLetterOrDigit(part[i - 1]))
                && part.IndexOf(c, i + 1) is var italicEnd and > 0 && !char.IsWhiteSpace(part[italicEnd - 1])
                && (italicEnd + 1 >= part.Length || !char.IsLetterOrDigit(part[italicEnd + 1])))
            {
                Flush();
                result.Add(new Run(part.Substring(i + 1, italicEnd - i - 1)) { FontStyle = FontStyles.Italic });
                i = italicEnd;
                continue;
            }
            if (c == '[' && part.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0 && part.IndexOf(')', mid) is var end and > 0
                && part.IndexOf('\n', i + 1, mid - i - 1) < 0)
            {
                Flush();
                result.Add(Link(new Run(part.Substring(i + 1, mid - i - 1)), part.Substring(mid + 2, end - mid - 2).Trim()));
                i = end;
                continue;
            }
            plain.Append(c);
        }
        Flush();
        return result;
    }

    Hyperlink Link(Inline content, string target)
    {
        var link = new Hyperlink(content) { ToolTip = target, Cursor = Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
        link.Click += (_, e) =>
        {
            e.Handled = true;
            Activate(target);
        };
        return link;
    }

    void Activate(string target)
    {
        if (GetLinkCommand(this) is { } command && command.CanExecute(target))
            command.Execute(target);
    }

    /// <summary>Fenced code: a header with the language and a Copy button (it says "Copied" for a moment), then the code.</summary>
    Block CodeBlock(MdCode code)
    {
        var label = new TextBlock { Text = Loc.Instance.Translate("Copy"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        var icon = new VsIcon { Kind = "Copy", Width = 13, Height = 13 };
        var copy = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = Loc.Instance.Translate("Copy the code"),
            Cursor = Cursors.Hand,
        };
        copy.SetResourceReference(StyleProperty, "Pill");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code.Text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard is busy; the user can select and copy instead.
                return;
            }
            icon.Kind = "Check";
            label.Text = Loc.Instance.Translate("Copied");
            var reset = new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                icon.Kind = "Copy";
                label.Text = Loc.Instance.Translate("Copy");
            }, Dispatcher);
            reset.Start();
        };
        var header = new DockPanel { LastChildFill = false, Background = CodeHeaderBackground };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var language = new TextBlock
        {
            Text = code.Language.Length > 0 ? code.Language : "code",
            FontSize = 11,
            FontFamily = CodeFont,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 4, 0, 4),
        };
        language.SetResourceReference(TextBlock.ForegroundProperty, "ClaudeMutedBrush");
        header.Children.Add(language);
        var body = new TextBox { Text = code.Text, FontFamily = CodeFont, FontSize = 12.5, Margin = new Thickness(12, 9, 12, 10) };
        body.SetResourceReference(StyleProperty, "ClaudeSelectableText");
        body.TextWrapping = TextWrapping.NoWrap;
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        });
        var border = new Border
        {
            Background = CodeBackground,
            CornerRadius = new CornerRadius(8),
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(1),
            Child = panel,
            ClipToBounds = true,
        };
        return new BlockUIContainer(border) { Margin = new Thickness(0, 2, 0, 12) };
    }

    Block List(MdList list)
    {
        var top = new List { MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0), Foreground = Accent };
        if (list.Ordered)
            top.StartIndex = list.Start;
        var stack = new List<List> { top };
        foreach (var item in list.Items)
        {
            var level = Math.Min(item.Level, stack.Count);
            while (stack.Count > level + 1)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count == level && stack[^1].ListItems.LastListItem is { } parent)
            {
                var nested = new List { MarkerStyle = level % 2 == 1 ? TextMarkerStyle.Circle : TextMarkerStyle.Disc, Margin = new Thickness(0), Padding = new Thickness(18, 0, 0, 0) };
                parent.Blocks.Add(nested);
                stack.Add(nested);
            }
            var text = item.Checked switch { true => "☑ " + item.Text, false => "☐ " + item.Text, null => item.Text };
            var paragraph = Paragraph(text, margin: new Thickness(0, 0, 0, 4));
            paragraph.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            stack[^1].ListItems.Add(new ListItem(paragraph));
        }
        return top;
    }

    Block Table(MdTable table)
    {
        var columns = table.Rows.Max(r => r.Count);
        var result = new Table { CellSpacing = 0, BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
        for (var c = 0; c < columns; c++)
            result.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0)
                row.Background = HeaderBackground;
            for (var c = 0; c < columns; c++)
            {
                var text = c < table.Rows[r].Count ? table.Rows[r][c] : "";
                row.Cells.Add(new TableCell(Paragraph(text, 12, r == 0 ? FontWeights.SemiBold : null, new Thickness(0)))
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                });
            }
            group.Rows.Add(row);
        }
        result.RowGroups.Add(group);
        return result;
    }
}
