using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using VSL.Git;

namespace VSL.Editor;

public enum DiffRowKind
{
    Equal,
    Removed,
    Added,
    Modified,
}

/// <summary>One aligned row of a side-by-side diff; -1 marks a filler line on that side.</summary>
public readonly record struct DiffRow(int Left, int Right, DiffRowKind Kind);

/// <summary>Aligns two texts into rows so unchanged lines sit side by side, like VS's diff viewer.</summary>
public static class SideBySide
{
    public static IReadOnlyList<DiffRow> Align(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var rows = new List<DiffRow>();
        int l = 0, r = 0;
        foreach (var h in LineDiff.Hunks(left, right))
        {
            while (l < h.BaseStart)
                rows.Add(new DiffRow(l++, r++, DiffRowKind.Equal));
            var paired = Math.Min(h.BaseCount, h.CurrentCount);
            for (var i = 0; i < paired; i++)
                rows.Add(new DiffRow(l++, r++, DiffRowKind.Modified));
            for (var i = paired; i < h.BaseCount; i++)
                rows.Add(new DiffRow(l++, -1, DiffRowKind.Removed));
            for (var i = paired; i < h.CurrentCount; i++)
                rows.Add(new DiffRow(-1, r++, DiffRowKind.Added));
        }
        while (l < left.Count || r < right.Count)
            rows.Add(new DiffRow(l < left.Count ? l++ : -1, r < right.Count ? r++ : -1, DiffRowKind.Equal));
        return rows;
    }

    /// <summary>First row of every contiguous block of changes, for Next/Previous Change.</summary>
    public static IReadOnlyList<int> ChangeStarts(IReadOnlyList<DiffRow> rows) =>
        Enumerable.Range(0, rows.Count).Where(i => rows[i].Kind != DiffRowKind.Equal && (i == 0 || rows[i - 1].Kind == DiffRowKind.Equal)).ToList();
}

/// <summary>Visual Studio style side-by-side comparison: baseline on the left, current text on the right.</summary>
public sealed class DiffView : DockPanel
{
    static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#4B1F1F"));
    static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#1F3B23"));
    static readonly IBrush FillerBrush = new SolidColorBrush(Color.Parse("#262626"));
    static readonly IBrush FillerHatch = new SolidColorBrush(Color.Parse("#303030"));
    static readonly IBrush RemovedTextBrush = new SolidColorBrush(Color.Parse("#8A3434"));
    static readonly IBrush AddedTextBrush = new SolidColorBrush(Color.Parse("#2E7D3A"));

    readonly TextEditor _left;
    readonly TextEditor _right;
    readonly IReadOnlyList<int> _changeStarts;
    readonly TextBlock _position;
    bool _syncing;
    int _current = -1;

    public DiffView(string filePath, string leftText, string rightText, string leftLabel, string rightLabel)
    {
        var leftLines = LineDiff.SplitLines(leftText);
        var rightLines = LineDiff.SplitLines(rightText);
        Rows = SideBySide.Align(leftLines, rightLines);
        _changeStarts = SideBySide.ChangeStarts(Rows);
        Added = Rows.Count(r => r.Kind == DiffRowKind.Added) + Rows.Count(r => r.Kind == DiffRowKind.Modified);
        Removed = Rows.Count(r => r.Kind == DiffRowKind.Removed) + Rows.Count(r => r.Kind == DiffRowKind.Modified);

        var leftRows = Rows.Select(r => r.Left >= 0 ? leftLines[r.Left] : "").ToList();
        var rightRows = Rows.Select(r => r.Right >= 0 ? rightLines[r.Right] : "").ToList();
        // Inside a modified line, the characters that actually differ (common prefix/suffix trimmed).
        var spans = Rows.Select((r, i) => r.Kind == DiffRowKind.Modified ? ChangedSpan(leftRows[i], rightRows[i]) : default).ToList();
        _left = CreateSide(filePath, leftRows, spans.Select(s => s.Left).ToList(), left: true);
        _right = CreateSide(filePath, rightRows, spans.Select(s => s.Right).ToList(), left: false);
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        _position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(8, 0) };
        UpdatePosition();
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Background = new SolidColorBrush(Color.Parse("#2D2D30")),
            Height = 26,
            Children =
            {
                Caption(leftLabel, 0),
                new StackPanel
                {
                    [Grid.ColumnProperty] = 1,
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        NavButton("Previous Change", "Push", () => Go(-1)),
                        NavButton("Next Change", "Pull", () => Go(1)),
                        _position,
                    },
                },
                Caption(rightLabel, 2),
            },
        };
        SetDock(header, Dock.Top);
        Children.Add(header);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        body.Children.Add(_left);
        body.Children.Add(new GridSplitter { [Grid.ColumnProperty] = 1, ResizeDirection = GridResizeDirection.Columns });
        Grid.SetColumn(_right, 2);
        body.Children.Add(_right);
        Children.Add(body);
    }

    public IReadOnlyList<DiffRow> Rows { get; }

    public int ChangeCount => _changeStarts.Count;

    public int Added { get; }

    public int Removed { get; }

    public string LeftText => _left.Document.Text;

    public string RightText => _right.Document.Text;

    static Control Caption(string text, int column) => new TextBlock
    {
        [Grid.ColumnProperty] = column,
        Text = text,
        Margin = new Thickness(10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")),
    };

    static Button NavButton(string tip, string icon, Action action)
    {
        var button = new Button { Classes = { "tbi" }, Content = new Views.VsIcon { Kind = icon } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    static ((int Start, int Length) Left, (int Start, int Length) Right) ChangedSpan(string a, string b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            suffix++;
        return ((prefix, a.Length - prefix - suffix), (prefix, b.Length - prefix - suffix));
    }

    TextEditor CreateSide(string filePath, IReadOnlyList<string> lines, IReadOnlyList<(int Start, int Length)> spans, bool left)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join("\n", lines)),
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = Application.Current?.FindResource("VsCodeFont") as FontFamily ?? new FontFamily("monospace"),
            FontSize = EditorHost.BaseFontSize * EditorHost.DefaultZoom,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Foreground = new SolidColorBrush(Color.Parse("#DCDCDC")),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        editor.TextArea.TextView.BackgroundRenderers.Add(new RowRenderer(Rows, spans, left));
        editor.TextArea.LeftMargins.Insert(0, new RowNumberMargin(Rows, left));
        TextMateSupport.Install(editor, filePath);
        return editor;
    }

    void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing)
            return;
        _syncing = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _syncing = false;
    }

    /// <summary>Moves to the next (+1) or previous (-1) block of changes, wrapping around.</summary>
    public void Go(int direction)
    {
        if (_changeStarts.Count == 0)
            return;
        _current = (_current + direction + _changeStarts.Count) % _changeStarts.Count;
        var line = _changeStarts[_current] + 1;
        _right.ScrollToLine(line);
        _right.TextArea.Caret.Line = line;
        _left.ScrollToLine(line);
        UpdatePosition();
    }

    public int CurrentChange => _current;

    void UpdatePosition() => _position.Text = _changeStarts.Count == 0
        ? "No differences"
        : _current < 0 ? $"{_changeStarts.Count} change(s): +{Added} −{Removed}" : $"Change {_current + 1} of {_changeStarts.Count}";

    /// <summary>Red rows on the left (removed), green on the right (added), shaded fillers where the other side has lines.</summary>
    sealed class RowRenderer(IReadOnlyList<DiffRow> rows, IReadOnlyList<(int Start, int Length)> spans, bool left) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid)
                return;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count)
                    continue;
                var row = rows[index];
                var filler = (left ? row.Left : row.Right) < 0;
                var rect = new Rect(0, visual.VisualTop - textView.VerticalOffset, textView.Bounds.Width, visual.Height);
                if (filler)
                {
                    dc.FillRectangle(FillerBrush, rect);
                    for (var x = -rect.Height; x < rect.Width; x += 8)
                        dc.DrawLine(new Pen(FillerHatch, 1), new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                }
                else if (row.Kind != DiffRowKind.Equal)
                {
                    dc.FillRectangle(left ? RemovedBrush : AddedBrush, rect);
                    if (row.Kind == DiffRowKind.Modified && spans[index].Length > 0)
                    {
                        var line = visual.FirstDocumentLine;
                        var segment = new AvaloniaEdit.Document.TextSegment
                        {
                            StartOffset = line.Offset + Math.Min(spans[index].Start, line.Length),
                            EndOffset = line.Offset + Math.Min(spans[index].Start + spans[index].Length, line.Length),
                        };
                        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                            dc.FillRectangle(left ? RemovedTextBrush : AddedTextBrush, r);
                    }
                }
            }
        }
    }

    /// <summary>Real file line numbers (fillers have none), in VS's line-number color.</summary>
    sealed class RowNumberMargin(IReadOnlyList<DiffRow> rows, bool left) : AbstractMargin
    {
        static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#2B91AF"));

        protected override Size MeasureOverride(Size availableSize) => new(48, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
                oldTextView.VisualLinesChanged -= Invalidate;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
                newTextView.VisualLinesChanged += Invalidate;
        }

        void Invalidate(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext dc)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid)
                return;
            var typeface = new Typeface(view.GetValue(TextBlock.FontFamilyProperty));
            var size = view.GetValue(TextBlock.FontSizeProperty);
            foreach (var visual in view.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count)
                    continue;
                var number = left ? rows[index].Left : rows[index].Right;
                if (number < 0)
                    continue;
                var text = new FormattedText((number + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, size, NumberBrush);
                dc.DrawText(text, new Point(Bounds.Width - text.Width - 10, visual.VisualTop - view.VerticalOffset));
            }
        }
    }
}
