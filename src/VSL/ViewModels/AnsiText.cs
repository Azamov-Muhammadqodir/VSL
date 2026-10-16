using System.Text;
using Avalonia.Media;

namespace VSL.ViewModels;

/// <summary>A colored run of Output text (offsets into the channel's document).</summary>
public readonly record struct OutputSpan(int Start, int Length, Color? Foreground, Color? Background);

/// <summary>
/// Turns console output with ANSI escape sequences (SGR colors, as `dotnet run` and the console logger write them)
/// into plain text plus colored runs, the way the integrated terminal shows them. Other escape sequences are dropped.
/// A sequence cut between two chunks is kept until the next one.
/// </summary>
public sealed class AnsiText
{
    /// <summary>The integrated terminal's 16 colors (Terminal/TerminalSession), so both windows look the same.</summary>
    public static readonly Color[] Palette =
    [
        Color.Parse("#000000"), Color.Parse("#CD3131"), Color.Parse("#0DBC79"), Color.Parse("#E5E510"),
        Color.Parse("#2472C8"), Color.Parse("#BC3FBC"), Color.Parse("#11A8CD"), Color.Parse("#E5E5E5"),
        Color.Parse("#666666"), Color.Parse("#F14C4C"), Color.Parse("#23D18B"), Color.Parse("#F5F543"),
        Color.Parse("#3B8EEA"), Color.Parse("#D670D6"), Color.Parse("#29B8DB"), Color.Parse("#FFFFFF"),
    ];

    string _pending = "";
    int? _foreground;
    Color? _foregroundRgb;
    int? _background;
    Color? _backgroundRgb;
    bool _bold;

    public void Reset()
    {
        _pending = "";
        _foreground = _background = null;
        _foregroundRgb = _backgroundRgb = null;
        _bold = false;
    }

    /// <summary>The plain text of <paramref name="chunk"/> and its colored runs, offsets relative to that text.</summary>
    public (string Text, List<OutputSpan> Spans) Parse(string chunk)
    {
        var input = _pending + chunk;
        _pending = "";
        var text = new StringBuilder(input.Length);
        var spans = new List<OutputSpan>();
        var runStart = 0;
        void CloseRun()
        {
            var (fg, bg) = (Foreground, Background);
            if (text.Length > runStart && (fg is not null || bg is not null))
                spans.Add(new OutputSpan(runStart, text.Length - runStart, fg, bg));
            runStart = text.Length;
        }
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c != '\u001b')
            {
                if (c != '\r')
                    text.Append(c);
                continue;
            }
            if (i + 1 >= input.Length)
            {
                _pending = input[i..];
                break;
            }
            var kind = input[i + 1];
            if (kind == '[')
            {
                // CSI: parameters and intermediates up to a final byte in @..~.
                var end = i + 2;
                while (end < input.Length && input[end] is < '@' or > '~')
                    end++;
                if (end >= input.Length)
                {
                    _pending = input[i..];
                    break;
                }
                if (input[end] == 'm')
                {
                    CloseRun();
                    ApplySgr(input[(i + 2)..end]);
                }
                i = end;
            }
            else if (kind == ']')
            {
                // OSC (window title, hyperlinks): up to BEL or ESC \.
                var bell = input.IndexOf('\u0007', i + 2);
                var st = input.IndexOf("\u001b\\", i + 2, StringComparison.Ordinal);
                var end = bell < 0 ? st : st < 0 ? bell : Math.Min(bell, st);
                if (end < 0)
                {
                    _pending = input[i..];
                    break;
                }
                i = end == st ? end + 1 : end;
            }
            else
            {
                i++;
            }
        }
        CloseRun();
        return (text.ToString(), spans);
    }

    Color? Foreground => _foregroundRgb ?? (_foreground is { } f ? Palette[f < 8 && _bold ? f + 8 : f] : null);

    Color? Background => _backgroundRgb ?? (_background is { } b ? Palette[b] : null);

    void ApplySgr(string parameters)
    {
        var codes = parameters.Length == 0 ? [0] : parameters.Split(';').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0:
                    _foreground = _background = null;
                    _foregroundRgb = _backgroundRgb = null;
                    _bold = false;
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case >= 30 and <= 37:
                    (_foreground, _foregroundRgb) = (code - 30, null);
                    break;
                case 39:
                    (_foreground, _foregroundRgb) = (null, null);
                    break;
                case >= 40 and <= 47:
                    (_background, _backgroundRgb) = (code - 40, null);
                    break;
                case 49:
                    (_background, _backgroundRgb) = (null, null);
                    break;
                case >= 90 and <= 97:
                    (_foreground, _foregroundRgb) = (code - 90 + 8, null);
                    break;
                case >= 100 and <= 107:
                    (_background, _backgroundRgb) = (code - 100 + 8, null);
                    break;
                case 38 or 48 when i + 1 < codes.Length:
                    Color? color = null;
                    if (codes[i + 1] == 5 && i + 2 < codes.Length)
                    {
                        color = Color256(codes[i + 2]);
                        i += 2;
                    }
                    else if (codes[i + 1] == 2 && i + 4 < codes.Length)
                    {
                        color = Color.FromRgb((byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4]);
                        i += 4;
                    }
                    if (code == 38)
                        (_foreground, _foregroundRgb) = (null, color);
                    else
                        (_background, _backgroundRgb) = (null, color);
                    break;
            }
        }
    }

    static Color Color256(int n)
    {
        if (n < 16)
            return Palette[Math.Max(n, 0)];
        if (n >= 232)
        {
            var gray = (byte)(8 + (Math.Min(n, 255) - 232) * 10);
            return Color.FromRgb(gray, gray, gray);
        }
        n -= 16;
        static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
        return Color.FromRgb(Level(n / 36), Level(n / 6 % 6), Level(n % 6));
    }
}
