using System.Text.Json;

namespace VSL.Lsp;

public readonly record struct LspPosition(int Line, int Character);

public readonly record struct LspRange(LspPosition Start, LspPosition End);

/// <summary>textDocument/foldingRange: zero-based lines; columns only when the server gives them.</summary>
public sealed record LspFoldingRange(int StartLine, int? StartCharacter, int EndLine, int? EndCharacter, string? Kind, string? CollapsedText);

public sealed record LspLocation(string Uri, LspRange Range)
{
    public string FilePath => new Uri(Uri).LocalPath;
}

public sealed record LspTextEdit(LspRange Range, string NewText);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>A diagnostic; <see cref="Raw"/> is the server's original object, which code actions need back verbatim.</summary>
public sealed record LspDiagnostic(LspRange Range, DiagnosticSeverity Severity, string Code, string Message, bool IsUnnecessary, JsonElement Raw = default);

/// <summary>A Quick Action / refactoring offered by the server; resolved lazily into a workspace edit.</summary>
public sealed record LspCodeAction(string Title, string? Kind, bool IsPreferred, JsonElement Raw, IReadOnlyList<LspCodeAction> Children);

/// <summary>One step of a workspace edit, applied in order: create/rename/delete a file or edit its text.</summary>
public sealed record LspFileOperation(string Kind, string Path, string? NewPath, IReadOnlyList<LspTextEdit> Edits);

/// <summary>A content change for textDocument/didChange; a null range means full-document replace.</summary>
public sealed record LspContentChange(LspRange? Range, string Text);

public sealed record LspCompletionItem(
    string Label,
    string? FilterText,
    string? SortText,
    string? InsertText,
    bool IsSnippet,
    int Kind,
    string? Detail,
    LspTextEdit? TextEdit,
    IReadOnlyList<LspTextEdit> AdditionalTextEdits,
    JsonElement Raw)
{
    public bool NeedsResolve => Raw.TryGetProperty("data", out _);
}

public sealed record LspCompletionList(bool IsIncomplete, IReadOnlyList<LspCompletionItem> Items);

public sealed record LspSignatureHelp(IReadOnlyList<LspSignature> Signatures, int ActiveSignature, int ActiveParameter);

public sealed record LspSignature(string Label, string? Documentation, IReadOnlyList<string> Parameters);

/// <summary>Parsing helpers for the loosely-typed LSP payloads Roslyn returns.</summary>
public static class LspParse
{
    public static LspPosition Position(JsonElement e) =>
        new(e.GetProperty("line").GetInt32(), e.GetProperty("character").GetInt32());

    public static LspRange Range(JsonElement e) =>
        new(Position(e.GetProperty("start")), Position(e.GetProperty("end")));

    public static LspTextEdit TextEdit(JsonElement e)
    {
        // InsertReplaceEdit has insert/replace instead of range; prefer replace like VS does.
        var range = e.TryGetProperty("range", out var r) ? r : e.GetProperty("replace");
        return new LspTextEdit(Range(range), e.GetProperty("newText").GetString() ?? "");
    }

    public static IReadOnlyList<LspTextEdit> TextEdits(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(TextEdit).ToList() : [];

    public static IReadOnlyList<LspLocation> Locations(JsonElement e)
    {
        var list = new List<LspLocation>();
        if (e.ValueKind == JsonValueKind.Object)
            AddLocation(e, list);
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                AddLocation(item, list);
        return list;
    }

    static void AddLocation(JsonElement e, List<LspLocation> list)
    {
        // Location or LocationLink.
        if (e.TryGetProperty("uri", out var uri))
            list.Add(new LspLocation(uri.GetString()!, Range(e.GetProperty("range"))));
        else if (e.TryGetProperty("targetUri", out var target))
            list.Add(new LspLocation(target.GetString()!, Range(e.GetProperty("targetSelectionRange"))));
    }

    public static IReadOnlyList<LspDiagnostic> Diagnostics(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<LspDiagnostic>();
        foreach (var d in items.EnumerateArray())
        {
            var severity = d.TryGetProperty("severity", out var s) ? (DiagnosticSeverity)s.GetInt32() : DiagnosticSeverity.Error;
            var code = d.TryGetProperty("code", out var c) ? c.ToString() : "";
            var unnecessary = d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                && tags.EnumerateArray().Any(t => t.GetInt32() == 1);
            list.Add(new LspDiagnostic(Range(d.GetProperty("range")), severity, code, d.GetProperty("message").GetString() ?? "", unnecessary, d));
        }
        return list;
    }

    public static LspCompletionList Completion(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Array)
            return new LspCompletionList(false, result.EnumerateArray().Select(i => CompletionItem(i, default)).ToList());
        if (result.ValueKind != JsonValueKind.Object)
            return new LspCompletionList(false, []);

        var defaults = result.TryGetProperty("itemDefaults", out var d) ? d : default;
        var items = result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(i => CompletionItem(i, defaults)).ToList()
            : [];
        var incomplete = result.TryGetProperty("isIncomplete", out var inc) && inc.GetBoolean();
        return new LspCompletionList(incomplete, items);
    }

    public static LspCompletionItem CompletionItem(JsonElement i, JsonElement defaults)
    {
        string? Str(string name) => i.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var label = Str("label") ?? "";
        LspTextEdit? edit = null;
        if (i.TryGetProperty("textEdit", out var te))
        {
            edit = TextEdit(te);
        }
        else if (defaults.ValueKind == JsonValueKind.Object && defaults.TryGetProperty("editRange", out var er))
        {
            // itemDefaults.editRange applies with textEditText (or label) as the new text.
            var range = er.TryGetProperty("replace", out var rep) ? Range(rep) : Range(er);
            edit = new LspTextEdit(range, Str("textEditText") ?? Str("insertText") ?? label);
        }

        var format = i.TryGetProperty("insertTextFormat", out var f) ? f.GetInt32()
            : defaults.ValueKind == JsonValueKind.Object && defaults.TryGetProperty("insertTextFormat", out var df) ? df.GetInt32() : 1;
        var additional = i.TryGetProperty("additionalTextEdits", out var ate) ? TextEdits(ate) : [];
        var kind = i.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;

        // Roslyn relies on itemDefaults.data for resolve; fold it into the item so resolve round-trips.
        var raw = i;
        if (!i.TryGetProperty("data", out _) && defaults.ValueKind == JsonValueKind.Object && defaults.TryGetProperty("data", out var dd))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(i.GetRawText())!.AsObject();
            node["data"] = System.Text.Json.Nodes.JsonNode.Parse(dd.GetRawText());
            raw = JsonSerializer.SerializeToElement(node);
        }

        return new LspCompletionItem(label, Str("filterText"), Str("sortText"), Str("insertText"), format == 2, kind,
            Str("detail"), edit, additional, raw);
    }

    /// <summary>Parses textDocument/codeAction results; Roslyn groups variants under data.NestedCodeActions.</summary>
    public static IReadOnlyList<LspCodeAction> CodeActions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
            return [];
        return result.EnumerateArray().Select(CodeAction).OfType<LspCodeAction>().ToList();
    }

    static LspCodeAction? CodeAction(JsonElement a)
    {
        // Bare Commands (title + command string) have no edit to apply; VSL only shows code actions.
        if (!a.TryGetProperty("title", out var title) || (a.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String))
            return null;
        var children = a.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("NestedCodeActions", out var nested) && nested.ValueKind == JsonValueKind.Array
                ? nested.EnumerateArray().Select(CodeAction).OfType<LspCodeAction>().ToList()
                : [];
        return new LspCodeAction(
            title.GetString() ?? "",
            a.TryGetProperty("kind", out var k) ? k.GetString() : null,
            a.TryGetProperty("isPreferred", out var p) && p.ValueKind == JsonValueKind.True,
            a,
            children);
    }

    /// <summary>Flattens a WorkspaceEdit (documentChanges or changes) into ordered file operations.</summary>
    public static IReadOnlyList<LspFileOperation> WorkspaceEdit(JsonElement edit)
    {
        var ops = new List<LspFileOperation>();
        if (edit.ValueKind != JsonValueKind.Object)
            return ops;
        static string PathOf(string uri) => new Uri(uri).LocalPath;
        if (edit.TryGetProperty("documentChanges", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in changes.EnumerateArray())
            {
                if (c.TryGetProperty("textDocument", out var td))
                    ops.Add(new LspFileOperation("edit", PathOf(td.GetProperty("uri").GetString()!), null, TextEdits(c.GetProperty("edits"))));
                else if (c.TryGetProperty("kind", out var kind))
                    ops.Add(kind.GetString() switch
                    {
                        "rename" => new LspFileOperation("rename", PathOf(c.GetProperty("oldUri").GetString()!), PathOf(c.GetProperty("newUri").GetString()!), []),
                        var k => new LspFileOperation(k ?? "", PathOf(c.GetProperty("uri").GetString()!), null, []),
                    });
            }
        }
        else if (edit.TryGetProperty("changes", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in map.EnumerateObject())
                ops.Add(new LspFileOperation("edit", PathOf(file.Name), null, TextEdits(file.Value)));
        }
        return ops;
    }

    /// <summary>Extracts plain text from MarkupContent / MarkedString / string documentation.</summary>
    public static string? Markup(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Object when e.TryGetProperty("value", out var v) => v.GetString(),
        JsonValueKind.Array => string.Join("\n\n", e.EnumerateArray().Select(Markup).Where(s => !string.IsNullOrEmpty(s))),
        _ => null,
    };

    public static LspSignatureHelp? SignatureHelp(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("signatures", out var sigs) || sigs.GetArrayLength() == 0)
            return null;
        var list = new List<LspSignature>();
        foreach (var s in sigs.EnumerateArray())
        {
            var label = s.GetProperty("label").GetString() ?? "";
            var parameters = new List<string>();
            if (s.TryGetProperty("parameters", out var ps))
            {
                foreach (var p in ps.EnumerateArray())
                {
                    var pl = p.GetProperty("label");
                    parameters.Add(pl.ValueKind == JsonValueKind.Array
                        ? label[pl[0].GetInt32()..pl[1].GetInt32()]
                        : pl.GetString() ?? "");
                }
            }
            var doc = s.TryGetProperty("documentation", out var d) ? Markup(d) : null;
            list.Add(new LspSignature(label, doc, parameters));
        }
        var active = e.TryGetProperty("activeSignature", out var a) ? a.GetInt32() : 0;
        var activeParam = e.TryGetProperty("activeParameter", out var ap) ? ap.GetInt32() : 0;
        return new LspSignatureHelp(list, Math.Clamp(active, 0, list.Count - 1), activeParam);
    }
}
