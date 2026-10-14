using System.Collections.Concurrent;
using System.Text.Json;

namespace VSL.Lsp;

/// <summary>
/// Razor cohosting delegates the HTML half of .razor files to the client (VS and VS Code ship an HTML language
/// server for this). The server pushes each file's generated HTML via <c>razor/updateHtml</c> and then asks the client
/// for HTML results with ordinary <c>textDocument/*</c> requests on that virtual document. This is a small
/// HTML service that answers those requests; Razor merges our items with its own component/C# items.
/// </summary>
public sealed class HtmlLanguageService
{
    static readonly string[] Elements =
    [
        "a", "abbr", "address", "article", "aside", "audio", "b", "blockquote", "body", "br", "button", "canvas", "caption",
        "code", "col", "colgroup", "datalist", "dd", "details", "dialog", "div", "dl", "dt", "em", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "i", "iframe", "img",
        "input", "label", "legend", "li", "link", "main", "mark", "meta", "nav", "noscript", "ol", "optgroup", "option",
        "output", "p", "picture", "pre", "progress", "script", "section", "select", "small", "source", "span", "strong",
        "style", "sub", "summary", "sup", "svg", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead",
        "time", "title", "tr", "u", "ul", "video",
    ];

    static readonly string[] Attributes =
    [
        "accept", "action", "alt", "aria-label", "autocomplete", "autofocus", "checked", "class", "cols", "colspan",
        "content", "data-", "disabled", "download", "for", "form", "height", "hidden", "href", "id", "lang", "max",
        "maxlength", "method", "min", "multiple", "name", "pattern", "placeholder", "readonly", "rel", "required", "role",
        "rows", "rowspan", "selected", "size", "src", "step", "style", "tabindex", "target", "title", "type", "value", "width",
    ];

    readonly ConcurrentDictionary<string, string> _documents = new();

    /// <summary>Handles razor/updateHtml: stores the virtual HTML text for a Razor document.</summary>
    public void Update(JsonElement p)
    {
        var uri = p.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u) ? u.GetString()
            : p.TryGetProperty("uri", out var u2) ? u2.GetString() : null;
        if (uri is not null && p.TryGetProperty("text", out var text))
            _documents[uri] = text.GetString() ?? "";
    }

    public int DocumentCount => _documents.Count;

    /// <summary>Answers a server-to-client textDocument/* request on a virtual HTML document.</summary>
    public object? Handle(string method, JsonElement p) => method switch
    {
        "textDocument/completion" => Complete(p),
        "textDocument/formatting" or "textDocument/rangeFormatting" or "textDocument/onTypeFormatting" => Array.Empty<object>(),
        "textDocument/foldingRange" or "textDocument/documentHighlight" or "textDocument/documentSymbol"
            or "textDocument/documentColor" or "textDocument/codeAction" or "textDocument/inlayHint" => Array.Empty<object>(),
        "textDocument/diagnostic" => new { kind = "full", items = Array.Empty<object>() },
        _ => null,
    };

    object Complete(JsonElement p)
    {
        var empty = new { isIncomplete = false, items = Array.Empty<object>() };
        var uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
        if (!_documents.TryGetValue(uri, out var text) || !p.TryGetProperty("position", out var posElement))
            return empty;
        var offset = OffsetOf(text, LspParse.Position(posElement));
        var context = ContextAt(text, offset);
        var names = context switch
        {
            Context.TagName => Elements,
            Context.Attribute => Attributes,
            _ => [],
        };
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '-'))
            start--;
        var prefix = text[start..offset];
        return new
        {
            isIncomplete = false,
            items = names
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(n => new { label = n, kind = context == Context.TagName ? 10 : 5, sortText = "~" + n })
                .ToArray(),
        };
    }

    enum Context
    {
        None,
        TagName,
        Attribute,
    }

    static Context ContextAt(string text, int offset)
    {
        var i = offset - 1;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '-'))
            i--;
        if (i >= 0 && text[i] == '<')
            return Context.TagName;
        // Inside an open tag: the nearest '<' comes after the nearest '>' and we are not inside a quoted value.
        var lt = text.LastIndexOf('<', Math.Max(0, offset - 1));
        var gt = text.LastIndexOf('>', Math.Max(0, offset - 1));
        if (lt > gt && lt >= 0)
        {
            var quotes = text[lt..offset].Count(c => c == '"');
            return quotes % 2 == 0 && i >= 0 && char.IsWhiteSpace(text[i]) ? Context.Attribute : Context.None;
        }
        return Context.None;
    }

    static int OffsetOf(string text, LspPosition pos)
    {
        var offset = 0;
        for (var line = 0; line < pos.Line && offset < text.Length; line++)
        {
            var nl = text.IndexOf('\n', offset);
            if (nl < 0)
                return text.Length;
            offset = nl + 1;
        }
        return Math.Min(offset + pos.Character, text.Length);
    }
}
