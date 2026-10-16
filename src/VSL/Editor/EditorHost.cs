using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using VSL.Debugging;
using VSL.Lsp;

namespace VSL.Editor;

/// <summary>
/// One open file: the AvaloniaEdit control plus its language-server session (sync, IntelliSense, squiggles,
/// hover, navigation). Kept alive per document so undo history and scroll position survive tab switches.
/// </summary>
public sealed partial class EditorHost : IDisposable, IDebugLineSource
{
    static readonly IBrush EditorBackground = new SolidColorBrush(Color.Parse("#1E1E1E"));
    static readonly IBrush EditorForeground = new SolidColorBrush(Color.Parse("#DCDCDC"));
    static readonly IBrush LineNumberBrush = new SolidColorBrush(Color.Parse("#2B91AF"));
    static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#264F78"));

    readonly DiagnosticRenderer _diagnostics;
    readonly TextMate.Installation? _textMate;
    readonly List<LspContentChange> _pendingChanges = [];
    readonly Encoding _encoding;
    RoslynClient? _lsp;
    int _version = 1;
    bool _flushScheduled;
    bool _chordCtrlK;
    bool _chordCtrlM;
    CompletionWindow? _completionWindow;
    OverloadInsightWindow? _insightWindow;
    CancellationTokenSource? _diagnosticsCts;
    CancellationTokenSource? _completionCts;
    CancellationTokenSource? _hoverCts;
    BreakpointStore? _breakpoints;
    string[]? _gitBaseline;
    IReadOnlyList<Git.LineChange> _gitChanges = [];
    DispatcherTimer? _gitTimer;
    readonly List<TextAnchor> _breakpointAnchors = [];
    int? _executionLine;

    /// <summary>Editor font size at 100 % (Cascadia Mono 10 pt, Visual Studio's default).</summary>
    public const double BaseFontSize = 13.33;

    public const double MinZoom = 0.2, MaxZoom = 4.0;

    /// <summary>Zoom applied to newly opened documents; follows the last zoom the user picked.</summary>
    public static double DefaultZoom { get; set; } = 1.0;

    public EditorHost(string filePath)
    {
        FilePath = filePath;
        var bytes = File.ReadAllBytes(filePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        _encoding = new UTF8Encoding(hasBom);
        var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        LastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);

        Document = new TextDocument(text) { FileName = filePath };
        Document.UndoStack.ClearAll();
        Editor = new TextEditor
        {
            Document = Document,
            ShowLineNumbers = true,
            FontFamily = Application.Current?.FindResource("VsCodeFont") as FontFamily ?? new FontFamily("monospace"),
            FontSize = BaseFontSize * DefaultZoom,
            Background = EditorBackground,
            Foreground = EditorForeground,
            LineNumbersForeground = LineNumberBrush,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
        };
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.CutCopyWholeLine = true;
        Editor.TextArea.SelectionBrush = SelectionBrush;
        Editor.TextArea.TextView.CurrentLineBackground = Brushes.Transparent;
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.Parse("#464646")), 2);

        Editor.LineNumbersMargin = new Thickness(12, 0, 10, 0);

        _textMate = TextMateSupport.Install(Editor, filePath);
        _diagnostics = new DiagnosticRenderer(Document);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);

        Document.Changing += OnDocumentChanging;
        Document.Changed += (_, _) =>
        {
            SyncBreakpointsFromAnchors();
            ScheduleGitChanges();
        };
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                DirtyChanged?.Invoke();
        };
        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) => CaretMoved?.Invoke();
        Editor.PointerHover += OnPointerHover;
        Editor.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(Editor, false);
        Editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
        Editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Editor.TextArea.RightClickMovesCaret = true;
        Editor.ContextMenu = BuildContextMenu();
        InitializeOutlining();
        InitializeSemanticColors();
    }

    public string FilePath { get; }
    public TextDocument Document { get; }
    public TextEditor Editor { get; }
    public DateTime LastWriteTimeUtc { get; private set; }
    public bool IsDirty => !Document.UndoStack.IsOriginalFile;
    public bool HasLanguageServer => _lsp is not null;
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; private set; } = [];

    public event Action? DirtyChanged;
    public event Action? Saved;

    /// <summary>Git > Compare with Unmodified for this document.</summary>
    public event Action? CompareRequested;

    /// <summary>Ask Claude about this file or selection (opens the Claude Code window).</summary>
    public event Action? AskClaudeRequested;

    public bool HasGitBaseline => _gitBaseline is not null;
    public event Action? CaretMoved;
    public event Action? DiagnosticsChanged;
    public event Action<IReadOnlyList<LspLocation>>? DefinitionsFound;
    public event Action<string, IReadOnlyList<LspLocation>>? ReferencesFound;
    public event Action<string>? StatusMessage;
    public event Func<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>, Task>? WorkspaceEditRequested;
    public event Func<string, Task<string?>>? PromptRequested;

    /// <summary>Applies a code action's edit, which may touch other files or create/rename them.</summary>
    public event Func<IReadOnlyList<LspFileOperation>, Task>? WorkspaceOperationsRequested;

    // ---- Zoom (Ctrl+wheel, Ctrl+Shift+. / Ctrl+Shift+,) ------------------------------------------------------

    public double Zoom => Editor.FontSize / BaseFontSize;

    public event Action? ZoomChanged;

    public void SetZoom(double zoom)
    {
        zoom = Math.Clamp(Math.Round(zoom, 2), MinZoom, MaxZoom);
        if (Math.Abs(zoom - Zoom) < 0.001)
            return;
        Editor.FontSize = BaseFontSize * zoom;
        DefaultZoom = zoom;
        ZoomChanged?.Invoke();
    }

    /// <summary>Steps by 10 % like VS: up from 100 % to 110 %, down to 90 %.</summary>
    public void ZoomIn() => SetZoom(Math.Floor(Zoom * 10 + 0.001) / 10 + 0.1);

    public void ZoomOut() => SetZoom(Math.Ceiling(Zoom * 10 - 0.001) / 10 - 0.1);

    void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
            return;
        if (e.Delta.Y > 0)
            ZoomIn();
        else
            ZoomOut();
        e.Handled = true;
    }

    // ---- Git change margin ---------------------------------------------------------------------------------

    public IReadOnlyList<Git.LineChange> GitChanges => _gitChanges;

    /// <summary>
    /// Sets the HEAD version to compare against: null hides the margin (file outside a repository),
    /// an empty string marks every line as added (untracked file).
    /// </summary>
    public void SetGitBaseline(string? headText)
    {
        _gitBaseline = headText is null ? null : headText.Length == 0 ? [] : Git.LineDiff.SplitLines(headText);
        if (_gitBaseline is not null && !Editor.TextArea.LeftMargins.OfType<GitChangeMargin>().Any())
        {
            // Change bars sit between the line numbers and the outlining margin, as in VS.
            var margins = Editor.TextArea.LeftMargins;
            var folding = margins.OfType<FoldingMargin>().FirstOrDefault();
            margins.Insert(folding is null ? margins.Count : margins.IndexOf(folding), new GitChangeMargin(() => _gitChanges));
        }
        RecomputeGitChanges();
    }

    void ScheduleGitChanges()
    {
        if (_gitBaseline is null)
            return;
        _gitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _gitTimer!.Stop();
            RecomputeGitChanges();
        });
        _gitTimer.Stop();
        _gitTimer.Start();
    }

    void RecomputeGitChanges()
    {
        _gitChanges = _gitBaseline is null ? [] : Git.LineDiff.Compute(_gitBaseline, Git.LineDiff.SplitLines(Document.Text));
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<GitChangeMargin>())
            margin.InvalidateVisual();
    }

    // ---- Debugging ------------------------------------------------------------------------------------------

    /// <summary>Evaluates an expression for a hover data tip while the debugger is in break mode.</summary>
    public Func<string, Task<string?>>? DataTipProvider { get; set; }

    public static bool SupportsBreakpoints(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

    public void AttachBreakpoints(BreakpointStore store)
    {
        if (!SupportsBreakpoints(FilePath) || _breakpoints is not null)
            return;
        _breakpoints = store;
        store.Changed += OnBreakpointsChanged;
        store.VerificationChanged += OnVerificationChanged;
        Editor.TextArea.LeftMargins.Insert(0, new BreakpointMargin(this));
        Editor.TextArea.TextView.BackgroundRenderers.Add(new DebugLineRenderer(this));
        RebuildBreakpointAnchors();
    }

    public IReadOnlyCollection<int> BreakpointLines => _breakpoints?.For(FilePath) ?? [];

    public bool? IsBreakpointVerified(int line) => _breakpoints?.IsVerified(FilePath, line);

    public int? ExecutionLine => _executionLine;

    public void ToggleBreakpoint(int line) => _breakpoints?.Toggle(FilePath, line);

    /// <summary>Shows (or clears) the yellow current-statement marker and brings it into view.</summary>
    public void SetExecutionLine(int? line)
    {
        _executionLine = line;
        RedrawDebugAdornments();
        if (line is { } l && l >= 1 && l <= Document.LineCount)
        {
            RevealOffset(Document.GetLineByNumber(l).Offset);
            Editor.CaretOffset = Document.GetLineByNumber(l).Offset;
            Editor.ScrollToLine(l);
        }
    }

    void OnBreakpointsChanged(string file, bool fromEditor)
    {
        if (file != FilePath)
            return;
        if (!fromEditor)
            RebuildBreakpointAnchors();
        RedrawDebugAdornments();
    }

    void OnVerificationChanged(string file)
    {
        if (file == FilePath)
            RedrawDebugAdornments();
    }

    void RebuildBreakpointAnchors()
    {
        _breakpointAnchors.Clear();
        foreach (var line in BreakpointLines.Where(l => l >= 1 && l <= Document.LineCount))
        {
            var anchor = Document.CreateAnchor(Document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            anchor.SurviveDeletion = true;
            _breakpointAnchors.Add(anchor);
        }
    }

    /// <summary>Breakpoints follow their code when lines are inserted or removed above them.</summary>
    void SyncBreakpointsFromAnchors()
    {
        if (_breakpoints is null || _breakpointAnchors.Count == 0)
            return;
        _breakpoints.Replace(FilePath, _breakpointAnchors.Select(a => a.Line));
    }

    void RedrawDebugAdornments()
    {
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<BreakpointMargin>())
            margin.InvalidateVisual();
    }

    /// <summary>The expression under the pointer for a data tip: identifiers joined by dots (this.Order.Total).</summary>
    string? ExpressionAt(int offset)
    {
        if (offset >= Document.TextLength || !IsIdentifierChar(Document.GetCharAt(offset)))
            return null;
        var start = offset;
        while (start > 0 && (IsIdentifierChar(Document.GetCharAt(start - 1)) || Document.GetCharAt(start - 1) == '.'))
            start--;
        var end = offset;
        while (end < Document.TextLength && IsIdentifierChar(Document.GetCharAt(end)))
            end++;
        var text = Document.GetText(start, end - start).Trim('.');
        return text.Length == 0 || char.IsDigit(text[0]) ? null : text;
    }

    // ---- Language server session ------------------------------------------------------------------------

    public void Attach(RoslynClient lsp)
    {
        if (!RoslynClient.IsSupported(FilePath))
            return;
        _lsp = lsp;
        _pendingChanges.Clear();
        _version = 1;
        _ = lsp.DidOpenAsync(FilePath, Document.Text);
        ScheduleDiagnostics(200);
        ScheduleOutlining(300);
        ScheduleSemanticTokens(300);
    }

    public void Detach()
    {
        _lsp = null;
        _pendingChanges.Clear();
    }

    void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_lsp is null)
            return;
        // Positions are computed against the pre-change text, as LSP incremental sync requires.
        var start = TextPositions.ToPosition(Document, e.Offset);
        var end = TextPositions.ToPosition(Document, e.Offset + e.RemovalLength);
        _pendingChanges.Add(new LspContentChange(new LspRange(start, end), e.InsertedText.Text));
        if (!_flushScheduled)
        {
            _flushScheduled = true;
            Dispatcher.UIThread.Post(FlushChanges, DispatcherPriority.Background);
        }
    }

    /// <summary>Sends buffered edits; called before every request so the server sees what the user sees.</summary>
    public void FlushChanges()
    {
        _flushScheduled = false;
        if (_lsp is null || _pendingChanges.Count == 0)
            return;
        var changes = _pendingChanges.ToList();
        _pendingChanges.Clear();
        _version++;
        _ = _lsp.DidChangeAsync(FilePath, _version, changes);
        ScheduleDiagnostics();
    }

    public void ScheduleDiagnostics(int delayMs = 600)
    {
        if (_lsp is null)
            return;
        _diagnosticsCts?.Cancel();
        var cts = _diagnosticsCts = new CancellationTokenSource();
        _ = PullDiagnosticsAsync(delayMs, cts.Token);
    }

    async Task PullDiagnosticsAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            FlushChanges();
            if (_lsp is not { } lsp)
                return;
            var list = await lsp.DiagnosticsAsync(FilePath, ct);
            if (ct.IsCancellationRequested)
                return;
            Diagnostics = list;
            _diagnostics.SetDiagnostics(list);
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            DiagnosticsChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
        }
    }

    // ---- Typing: IntelliSense --------------------------------------------------------------------------

    static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    bool IsRazor => FilePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || FilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow is null || string.IsNullOrEmpty(e.Text))
            return;
        var c = e.Text[0];
        if (IsIdentifierChar(c))
            return;
        // VS commits the hard-selected item on punctuation, but only once the user has typed a filter.
        var typedFilter = Editor.CaretOffset > _completionWindow.StartOffset;
        if (typedFilter && ".;,()[]<>=+-*/&|!?: ".Contains(c) && _completionWindow.CompletionList.SelectedItem is not null)
            _completionWindow.CompletionList.RequestInsertion(e);
        else
            _completionWindow.Close();
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_lsp is null || string.IsNullOrEmpty(e.Text))
            return;
        var c = e.Text[^1];
        if (c == '.' || (IsRazor && (c == '<' || c == '@')))
        {
            RequestCompletion(c.ToString());
        }
        else if (IsIdentifierChar(c) && !char.IsDigit(c) && _completionWindow is null)
        {
            var before = Editor.CaretOffset - 2;
            if (before < 0 || !IsIdentifierChar(Document.GetCharAt(before)))
                RequestCompletion(null);
        }

        if (c is '(' or ',')
            RequestSignatureHelp();
        else if (c == ')')
            _insightWindow?.Close();
    }

    int FindWordStart(int offset)
    {
        var start = offset;
        while (start > 0 && IsIdentifierChar(Document.GetCharAt(start - 1)))
            start--;
        return start;
    }

    public async void RequestCompletion(string? triggerCharacter)
    {
        if (_lsp is not { } lsp)
            return;
        FlushChanges();
        var caret = Editor.CaretOffset;
        var line = Document.GetLineByOffset(caret).LineNumber;
        _completionCts?.Cancel();
        var cts = _completionCts = new CancellationTokenSource();
        LspCompletionList list;
        try
        {
            list = await lsp.CompletionAsync(FilePath, TextPositions.ToPosition(Document, caret), triggerCharacter, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or LspException or IOException)
        {
            return;
        }
        if (cts.IsCancellationRequested || list.Items.Count == 0)
            return;
        var now = Editor.CaretOffset;
        if (now < caret - 1 || Document.GetLineByOffset(now).LineNumber != line)
            return;

        var start = list.Items[0].TextEdit is { } edit
            ? Math.Min(TextPositions.ToOffset(Document, edit.Range.Start), now)
            : FindWordStart(caret);
        ShowCompletion(list, start);
    }

    void ShowCompletion(LspCompletionList list, int start)
    {
        _completionWindow?.Close();
        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = start,
            EndOffset = Editor.CaretOffset,
            CloseWhenCaretAtBeginning = false,
            CloseAutomatically = true,
        };
        var data = window.CompletionList.CompletionData;
        foreach (var item in list.Items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal))
            data.Add(new CompletionData(item, OnCompletionCommitted));
        window.Closed += (_, _) =>
        {
            if (_completionWindow == window)
                _completionWindow = null;
        };
        window.CompletionList.SelectionChanged += (_, _) => ResolveSelected(window);
        _completionWindow = window;
        window.Show();
        var typed = Document.GetText(start, Math.Max(0, Editor.CaretOffset - start));
        if (typed.Length > 0)
            window.CompletionList.SelectItem(typed);
    }

    async void ResolveSelected(CompletionWindow window)
    {
        if (_lsp is not { } lsp || window.CompletionList.SelectedItem is not CompletionData data || !data.Item.NeedsResolve)
            return;
        try
        {
            var (resolved, doc) = await lsp.ResolveCompletionAsync(data.Item, CancellationToken.None);
            data.SetDocumentation(resolved, doc);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
        }
    }

    /// <summary>After insertion, resolve the item for extra edits such as the "using" directive VS adds.</summary>
    async void OnCompletionCommitted(LspCompletionItem item, int insertionStart)
    {
        if (_lsp is not { } lsp)
            return;
        var edits = item.AdditionalTextEdits;
        if (edits.Count == 0 && item.NeedsResolve)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                edits = (await lsp.ResolveCompletionAsync(item, cts.Token)).Item.AdditionalTextEdits;
            }
            catch (Exception ex) when (ex is OperationCanceledException or LspException or IOException)
            {
                return;
            }
        }
        // Positions refer to the text before insertion; only edits above the insertion point are still valid.
        var applicable = edits
            .Select(e => (Edit: e, Start: TextPositions.ToOffset(Document, e.Range.Start), End: TextPositions.ToOffset(Document, e.Range.End)))
            .Where(e => e.End <= insertionStart)
            .OrderByDescending(e => e.Start)
            .ToList();
        if (applicable.Count == 0)
            return;
        Document.BeginUpdate();
        try
        {
            foreach (var (edit, s, e) in applicable)
                Document.Replace(s, e - s, edit.NewText);
        }
        finally
        {
            Document.EndUpdate();
        }
    }

    async void RequestSignatureHelp()
    {
        if (_lsp is not { } lsp)
            return;
        FlushChanges();
        LspSignatureHelp? help;
        try
        {
            help = await lsp.SignatureHelpAsync(FilePath, TextPositions.ToPosition(Document, Editor.CaretOffset), CancellationToken.None);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            return;
        }
        if (help is null)
        {
            _insightWindow?.Close();
            return;
        }
        if (_insightWindow is { } existing)
        {
            existing.Provider = new SignatureProvider(help);
            return;
        }
        var window = new OverloadInsightWindow(Editor.TextArea) { Provider = new SignatureProvider(help) };
        window.Closed += (_, _) => _insightWindow = null;
        _insightWindow = window;
        window.Show();
    }

    // ---- Hover --------------------------------------------------------------------------------------------

    async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is not { } p)
            return;
        var offset = Document.GetOffset(p.Location);
        var diagnostics = _diagnostics.At(offset).ToList();
        string? dataTip = null;
        if (DataTipProvider is { } provider && ExpressionAt(offset) is { } expression && await provider(expression) is { } value)
            dataTip = $"{expression} = {value}";
        string? hover = null;
        if (_lsp is { } lsp)
        {
            FlushChanges();
            _hoverCts?.Cancel();
            var cts = _hoverCts = new CancellationTokenSource();
            try
            {
                hover = await lsp.HoverAsync(FilePath, TextPositions.ToPosition(Document, offset), cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or LspException or IOException)
            {
                return;
            }
            if (cts.IsCancellationRequested)
                return;
        }
        if (string.IsNullOrWhiteSpace(hover) && diagnostics.Count == 0 && dataTip is null)
            return;
        ToolTip.SetTip(Editor, QuickInfo.Build(hover, diagnostics, dataTip));
        ToolTip.SetIsOpen(Editor, true);
    }

    // ---- Commands -------------------------------------------------------------------------------------------

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_chordCtrlM)
        {
            _chordCtrlM = false;
            e.Handled = true;
            if (!ctrl || !HandleOutliningChord(e.Key))
                StatusMessage?.Invoke("The key combination (Ctrl+M, " + e.Key + ") is not a command.");
            return;
        }
        if (_chordCtrlK)
        {
            _chordCtrlK = false;
            e.Handled = true;
            switch (e.Key)
            {
                case Key.D when ctrl: _ = FormatDocumentAsync(); break;
                case Key.C when ctrl: ToggleLineComment(comment: true); break;
                case Key.U when ctrl: ToggleLineComment(comment: false); break;
                default: StatusMessage?.Invoke("The key combination (Ctrl+K, " + e.Key + ") is not a command."); break;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.M when ctrl && !shift:
                _chordCtrlM = true;
                StatusMessage?.Invoke("(Ctrl+M) was pressed. Waiting for second key of chord...");
                e.Handled = true;
                break;
            case Key.K when ctrl && !shift:
                _chordCtrlK = true;
                StatusMessage?.Invoke("(Ctrl+K) was pressed. Waiting for second key of chord...");
                e.Handled = true;
                break;
            case Key.Space when ctrl && shift:
                RequestSignatureHelp();
                e.Handled = true;
                break;
            case Key.Space when ctrl:
            case Key.J when ctrl:
                RequestCompletion(null);
                e.Handled = true;
                break;
            case Key.F12 when shift:
                _ = FindReferencesAsync(Editor.CaretOffset);
                e.Handled = true;
                break;
            case Key.F12 when ctrl:
                _ = GoToAsync(Editor.CaretOffset, implementation: true);
                e.Handled = true;
                break;
            case Key.F12:
                _ = GoToAsync(Editor.CaretOffset, implementation: false);
                e.Handled = true;
                break;
            case Key.F2:
                _ = RenameAsync();
                e.Handled = true;
                break;
            case Key.OemPeriod when ctrl && !shift:
            case Key.Enter when e.KeyModifiers == KeyModifiers.Alt:
                ShowQuickActions();
                e.Handled = true;
                break;
            case Key.OemPeriod when ctrl && shift:
            case Key.OemPlus when ctrl:
            case Key.Add when ctrl:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.OemComma when ctrl && shift:
            case Key.OemMinus when ctrl:
            case Key.Subtract when ctrl:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                SetZoom(1.0);
                e.Handled = true;
                break;
            case Key.F9 when _breakpoints is not null:
                ToggleBreakpoint(Editor.TextArea.Caret.Line);
                e.Handled = true;
                break;
        }
    }

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed)
            return;
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is { } p)
        {
            e.Handled = true;
            _ = GoToAsync(Document.GetOffset(p.Location), implementation: false);
        }
    }

    // ---- Quick Actions (Ctrl+.) ---------------------------------------------------------------------------

    /// <summary>Code actions for the selection, or for the diagnostic under (or on the line of) the caret.</summary>
    internal async Task<IReadOnlyList<LspCodeAction>> GetQuickActionsAsync()
    {
        if (_lsp is not { } lsp)
            return [];
        FlushChanges();
        var caret = Editor.CaretOffset;
        var line = Document.GetLineByOffset(caret).LineNumber - 1;
        var diagnostics = _diagnostics.At(caret).ToList();
        if (diagnostics.Count == 0)
            diagnostics = Diagnostics.Where(d => d.Range.Start.Line <= line && d.Range.End.Line >= line).ToList();
        LspRange range;
        var selection = Editor.TextArea.Selection;
        if (!selection.IsEmpty)
            range = new LspRange(TextPositions.ToPosition(Document, selection.SurroundingSegment.Offset),
                TextPositions.ToPosition(Document, selection.SurroundingSegment.EndOffset));
        else if (diagnostics.Count > 0 && _diagnostics.At(caret).Any())
            range = diagnostics[0].Range;
        else
            range = new LspRange(TextPositions.ToPosition(Document, caret), TextPositions.ToPosition(Document, caret));
        try
        {
            return await lsp.CodeActionsAsync(FilePath, range, diagnostics, CancellationToken.None);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
            return [];
        }
    }

    public async void ShowQuickActions() => await ShowQuickActionsAsync();

    /// <summary>Opens the Quick Actions menu at the caret; returns it (null when there is nothing to offer).</summary>
    internal async Task<ContextMenu?> ShowQuickActionsAsync()
    {
        var actions = await GetQuickActionsAsync();
        if (actions.Count == 0)
        {
            StatusMessage?.Invoke("No quick actions available here.");
            return null;
        }
        var menu = new ContextMenu { ItemsSource = actions.Select(QuickActionItem).ToList() };
        var view = Editor.TextArea.TextView;
        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        var anchor = view.TranslatePoint(new Point(caret.X - view.HorizontalOffset, caret.Y - view.VerticalOffset), Editor) ?? default;
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementRect = new Rect(anchor.X, anchor.Y, 1, caret.Height);
        menu.Opened += (_, _) =>
        {
            if (menu.ItemsView.Count > 0 && menu.ContainerFromIndex(0) is MenuItem first)
                first.Focus(NavigationMethod.Directional);
        };
        menu.Closed += (_, _) => Editor.TextArea.Focus();
        menu.Open(Editor);
        return menu;
    }

    MenuItem QuickActionItem(LspCodeAction action)
    {
        var item = new MenuItem
        {
            Header = action.Title,
            Icon = new TextBlock { Text = "💡", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        };
        if (action.Children.Count > 0)
            item.ItemsSource = action.Children.Select(QuickActionItem).ToList();
        else
            item.Click += (_, _) => _ = ApplyCodeActionAsync(action);
        return item;
    }

    internal async Task ApplyCodeActionAsync(LspCodeAction action)
    {
        if (_lsp is not { } lsp)
            return;
        IReadOnlyList<LspFileOperation> ops;
        try
        {
            ops = await lsp.ResolveCodeActionAsync(action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
            return;
        }
        if (ops.Count == 0)
            return;
        if (WorkspaceOperationsRequested is not null)
            await WorkspaceOperationsRequested.Invoke(ops);
        else
            foreach (var op in ops.Where(o => o.Kind == "edit" && o.Path == FilePath))
                ApplyEdits(op.Edits);
        StatusMessage?.Invoke($"Applied: {action.Title}");
    }

    ContextMenu BuildContextMenu()
    {
        static object Header(string text, string gesture) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = text },
                new TextBlock { Text = gesture, Margin = new Thickness(32, 0, 0, 0), Foreground = Brushes.Gray, [Grid.ColumnProperty] = 1 },
            },
        };
        MenuItem Item(string text, string gesture, Action action)
        {
            var item = new MenuItem { Header = Header(text, gesture) };
            item.Click += (_, _) => action();
            return item;
        }
        var compare = Item("Compare with Unmodified", "", () => CompareRequested?.Invoke());
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Ask Claude", "Ctrl+Esc", () => AskClaudeRequested?.Invoke()),
                new Separator(),
                Item("Quick Actions and Refactorings...", "Ctrl+.", ShowQuickActions),
                Item("Rename...", "F2", () => _ = RenameAsync()),
                new Separator(),
                Item("Go To Definition", "F12", () => _ = GoToAsync(Editor.CaretOffset, implementation: false)),
                Item("Go To Implementation", "Ctrl+F12", () => _ = GoToAsync(Editor.CaretOffset, implementation: true)),
                Item("Find All References", "Shift+F12", () => _ = FindReferencesAsync(Editor.CaretOffset)),
                new Separator(),
                Item("Format Document", "Ctrl+K, Ctrl+D", () => _ = FormatDocumentAsync()),
                Item("Toggle Breakpoint", "F9", () => ToggleBreakpoint(Editor.TextArea.Caret.Line)),
                new MenuItem
                {
                    Header = "Outlining",
                    ItemsSource = new object[]
                    {
                        Item("Toggle Outlining Expansion", "Ctrl+M, Ctrl+M", ToggleOutliningExpansion),
                        Item("Toggle All Outlining", "Ctrl+M, Ctrl+L", ToggleAllOutlining),
                        Item("Collapse to Definitions", "Ctrl+M, Ctrl+O", CollapseToDefinitions),
                        Item("Expand All Outlining", "Ctrl+M, Ctrl+X", ExpandAllOutlining),
                    },
                },
                new Separator(),
                compare,
                new Separator(),
                Item("Cut", "Ctrl+X", Editor.Cut),
                Item("Copy", "Ctrl+C", Editor.Copy),
                Item("Paste", "Ctrl+V", Editor.Paste),
            },
        };
        menu.Opening += (_, _) => compare.IsEnabled = HasGitBaseline;
        return menu;
    }

    public async Task GoToAsync(int offset, bool implementation)
    {
        if (_lsp is not { } lsp)
            return;
        FlushChanges();
        try
        {
            var pos = TextPositions.ToPosition(Document, offset);
            var locations = implementation
                ? await lsp.ImplementationAsync(FilePath, pos, CancellationToken.None)
                : await lsp.DefinitionAsync(FilePath, pos, CancellationToken.None);
            if (locations.Count == 0)
                StatusMessage?.Invoke("Cannot navigate to the symbol under the caret.");
            else
                DefinitionsFound?.Invoke(locations);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
        }
    }

    public async Task FindReferencesAsync(int offset)
    {
        if (_lsp is not { } lsp)
            return;
        FlushChanges();
        var start = FindWordStart(offset);
        var end = offset;
        while (end < Document.TextLength && IsIdentifierChar(Document.GetCharAt(end)))
            end++;
        var symbol = Document.GetText(start, end - start);
        StatusMessage?.Invoke($"Finding references to '{symbol}'...");
        try
        {
            var refs = await lsp.ReferencesAsync(FilePath, TextPositions.ToPosition(Document, offset), CancellationToken.None);
            ReferencesFound?.Invoke(symbol, refs);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
        }
    }

    public async Task FormatDocumentAsync()
    {
        if (_lsp is not { } lsp)
            return;
        FlushChanges();
        try
        {
            var edits = await lsp.FormatAsync(FilePath, Editor.Options.IndentationSize, Editor.Options.ConvertTabsToSpaces, CancellationToken.None);
            ApplyEdits(edits);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
        }
    }

    async Task RenameAsync()
    {
        if (_lsp is not { } lsp || PromptRequested is null)
            return;
        var offset = Editor.CaretOffset;
        var start = FindWordStart(offset);
        var end = offset;
        while (end < Document.TextLength && IsIdentifierChar(Document.GetCharAt(end)))
            end++;
        if (end == start)
            return;
        var newName = await PromptRequested.Invoke(Document.GetText(start, end - start));
        if (string.IsNullOrWhiteSpace(newName))
            return;
        FlushChanges();
        try
        {
            var edits = await lsp.RenameAsync(FilePath, TextPositions.ToPosition(Document, offset), newName, CancellationToken.None);
            if (edits is null || edits.Count == 0)
                StatusMessage?.Invoke("This symbol cannot be renamed.");
            else if (WorkspaceEditRequested is not null)
                await WorkspaceEditRequested.Invoke(edits);
        }
        catch (Exception ex) when (ex is LspException or IOException)
        {
            StatusMessage?.Invoke(ex.Message);
        }
    }

    /// <summary>Applies LSP edits (all relative to the current text) as a single undo step.</summary>
    public void ApplyEdits(IReadOnlyList<LspTextEdit> edits)
    {
        if (edits.Count == 0)
            return;
        var resolved = edits
            .Select(e => (e.NewText, Start: TextPositions.ToOffset(Document, e.Range.Start), End: TextPositions.ToOffset(Document, e.Range.End)))
            .OrderByDescending(e => e.Start)
            .ToList();
        Document.BeginUpdate();
        try
        {
            foreach (var (text, s, e) in resolved)
                Document.Replace(s, e - s, text);
        }
        finally
        {
            Document.EndUpdate();
        }
    }

    void ToggleLineComment(bool comment)
    {
        var prefix = FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "//" : IsRazor ? "@*" : null;
        if (prefix is null)
            return;
        var suffix = prefix == "@*" ? "*@" : "";
        var selection = Editor.TextArea.Selection;
        var startLine = Document.GetLineByOffset(selection.IsEmpty ? Editor.CaretOffset : selection.SurroundingSegment.Offset).LineNumber;
        var endLine = Document.GetLineByOffset(selection.IsEmpty ? Editor.CaretOffset : selection.SurroundingSegment.EndOffset).LineNumber;
        Document.BeginUpdate();
        try
        {
            for (var n = endLine; n >= startLine; n--)
            {
                var line = Document.GetLineByNumber(n);
                var text = Document.GetText(line);
                var indent = text.Length - text.TrimStart().Length;
                if (comment)
                {
                    if (text.Trim().Length == 0)
                        continue;
                    if (suffix.Length > 0)
                        Document.Insert(line.EndOffset, suffix);
                    Document.Insert(line.Offset + indent, prefix);
                }
                else if (text.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (suffix.Length > 0 && text.TrimEnd().EndsWith(suffix, StringComparison.Ordinal))
                    {
                        var end = line.Offset + text.TrimEnd().Length - suffix.Length;
                        Document.Remove(end, suffix.Length);
                    }
                    Document.Remove(line.Offset + indent, prefix.Length);
                }
            }
        }
        finally
        {
            Document.EndUpdate();
        }
    }

    // ---- File I/O & navigation ---------------------------------------------------------------------------

    public void Save()
    {
        File.WriteAllText(FilePath, Document.Text, _encoding);
        LastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        Document.UndoStack.MarkAsOriginalFile();
        if (_lsp is { } lsp)
        {
            FlushChanges();
            _ = lsp.DidSaveAsync(FilePath);
        }
        Saved?.Invoke();
    }

    /// <summary>Reloads from disk when another tool (git, a formatter) changed the file and we have no edits.</summary>
    public bool ReloadIfChangedOnDisk()
    {
        if (!File.Exists(FilePath) || IsDirty)
            return false;
        var stamp = File.GetLastWriteTimeUtc(FilePath);
        if (stamp == LastWriteTimeUtc)
            return false;
        LastWriteTimeUtc = stamp;
        var text = File.ReadAllText(FilePath);
        if (text == Document.Text)
            return false;
        var caret = Editor.CaretOffset;
        Document.Replace(0, Document.TextLength, text);
        Document.UndoStack.MarkAsOriginalFile();
        Editor.CaretOffset = Math.Min(caret, Document.TextLength);
        return true;
    }

    public void NavigateTo(LspRange range)
    {
        var start = TextPositions.ToOffset(Document, range.Start);
        var end = TextPositions.ToOffset(Document, range.End);
        RevealOffset(start);
        Editor.Select(start, Math.Max(0, end - start));
        Editor.CaretOffset = start;
        var loc = Document.GetLocation(start);
        Editor.ScrollTo(loc.Line, loc.Column);
        Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Background);
    }

    public (int Line, int Column) CaretPosition => (Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);

    internal int CompletionItemCount => _completionWindow?.CompletionList.CompletionData.Count ?? 0;

    internal IEnumerable<string> CompletionLabels =>
        _completionWindow?.CompletionList.CompletionData.OfType<CompletionData>().Select(d => d.Item.Label) ?? [];

    internal void CloseCompletion() => _completionWindow?.Close();

    public void Dispose()
    {
        DisposeOutlining();
        _diagnosticsCts?.Cancel();
        _completionCts?.Cancel();
        _hoverCts?.Cancel();
        _completionWindow?.Close();
        _insightWindow?.Close();
        if (_lsp is { } lsp)
            _ = lsp.DidCloseAsync(FilePath);
        _lsp = null;
        _semanticCts?.Cancel();
        _textMate?.Dispose();
        _gitTimer?.Stop();
        if (_breakpoints is not null)
        {
            _breakpoints.Changed -= OnBreakpointsChanged;
            _breakpoints.VerificationChanged -= OnVerificationChanged;
        }
    }
}

/// <summary>Builds the Quick Info tooltip (hover text + diagnostics) in VS style.</summary>
public static class QuickInfo
{
    public static Control Build(string? markdown, IReadOnlyList<LspDiagnostic> diagnostics, string? dataTip = null)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 720 };
        if (dataTip is not null)
            panel.Children.Add(new SelectableTextBlock
            {
                Text = dataTip,
                FontFamily = Application.Current?.FindResource("VsCodeFont") as FontFamily ?? FontFamily.Default,
                Foreground = new SolidColorBrush(Color.Parse("#FFE400")),
                TextWrapping = TextWrapping.Wrap,
            });
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var parts = markdown.Split("```");
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (i % 2 == 1)
                {
                    // Code fence: drop the language tag on the first line.
                    var nl = part.IndexOf('\n');
                    var code = (nl >= 0 ? part[(nl + 1)..] : part).Trim();
                    if (code.Length > 0)
                        panel.Children.Add(new SelectableTextBlock
                        {
                            Text = code,
                            FontFamily = Application.Current?.FindResource("VsCodeFont") as FontFamily ?? FontFamily.Default,
                            Foreground = new SolidColorBrush(Color.Parse("#DCDCDC")),
                            TextWrapping = TextWrapping.Wrap,
                        });
                }
                else
                {
                    var prose = CleanMarkdown(part);
                    if (prose.Length > 0)
                        panel.Children.Add(new TextBlock { Text = prose, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")) });
                }
            }
        }
        foreach (var d in diagnostics)
        {
            var color = d.Severity switch
            {
                DiagnosticSeverity.Error => "#F14C4C",
                DiagnosticSeverity.Warning => "#4CC24C",
                _ => "#3794FF",
            };
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = d.Code, Foreground = new SolidColorBrush(Color.Parse(color)), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 },
                },
            });
        }
        if (diagnostics.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Show potential fixes (Ctrl+.)", Foreground = new SolidColorBrush(Color.Parse("#3794FF")) });
        return panel;
    }

    static string CleanMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && char.IsPunctuation(text[i + 1]))
            {
                sb.Append(text[++i]);
                continue;
            }
            if (c is '*' or '`')
                continue;
            sb.Append(c);
        }
        return string.Join("\n", sb.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)).Trim();
    }
}
