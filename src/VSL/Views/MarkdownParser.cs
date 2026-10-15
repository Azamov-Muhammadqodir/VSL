namespace VSL.Views;

/// <summary>A link inside a rendered paragraph: its character range and where it points.</summary>
public sealed record MdLink(int Start, int End, string Target);

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
