using Avalonia.Threading;
using VSL.Lsp;

namespace VSL.Editor;

/// <summary>Visual Studio's classification colors from Roslyn's semantic tokens, over the TextMate colors.</summary>
public sealed partial class EditorHost
{
    SemanticColorizer? _semantic;
    CancellationTokenSource? _semanticCts;
    int _semanticEdits;

    internal SemanticColorizer? SemanticColors => _semantic;

    void InitializeSemanticColors()
    {
        if (!RoslynClient.IsSupported(FilePath))
            return;
        // Added after TextMate's transformer, so the server's colors win where it has any.
        _semantic = new SemanticColorizer();
        Editor.TextArea.TextView.LineTransformers.Add(_semantic);
        Document.Changed += (_, e) =>
        {
            _semanticEdits++;
            _semantic.OnDocumentChanged(Document, e);
            ScheduleSemanticTokens();
        };
    }

    /// <summary>Asks for fresh tokens once edits settle (or right away after a project load or a server refresh).</summary>
    public void ScheduleSemanticTokens(int delayMs = 400)
    {
        if (_semantic is null || _lsp is null)
            return;
        _semanticCts?.Cancel();
        var cts = _semanticCts = new CancellationTokenSource();
        _ = UpdateSemanticTokensAsync(delayMs, cts.Token);
    }

    async Task UpdateSemanticTokensAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            if (_lsp is not { } lsp || _semantic is not { } semantic)
                return;
            FlushChanges();
            var edits = _semanticEdits;
            var data = await lsp.SemanticTokensAsync(FilePath, ct);
            // Tokens for an older text would land on the wrong columns; the newer edit has scheduled its own request.
            if (ct.IsCancellationRequested || edits != _semanticEdits)
                return;
            semantic.SetTokens(data, lsp.SemanticTokenTypes);
            Editor.TextArea.TextView.Redraw();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or LspException or System.Text.Json.JsonException)
        {
            // The server may be restarting or still loading; TextMate's colors stay until the next update.
        }
    }
}
