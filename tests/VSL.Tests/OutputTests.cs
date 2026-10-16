using Avalonia.Headless.XUnit;
using Avalonia.Media;
using VSL.ViewModels;

namespace VSL.Tests;

public class OutputTests
{
    static Color Palette(int i) => AnsiText.Palette[i];

    [Fact]
    public void Reads_the_console_loggers_colors_like_the_terminal()
    {
        var ansi = new AnsiText();
        // What Microsoft.Extensions.Logging.Console writes for an information and a warning line.
        var (text, spans) = ansi.Parse("\u001b[32minfo\u001b[39m\u001b[22m\u001b[49m: Microsoft.Hosting.Lifetime[14]\n"
            + "\u001b[1m\u001b[33mwarn\u001b[39m\u001b[22m\u001b[49m: Kestrel\n");
        Assert.Equal("info: Microsoft.Hosting.Lifetime[14]\nwarn: Kestrel\n", text);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new OutputSpan(0, 4, Palette(2), null), spans[0]);
        // Bold with a basic color is drawn bright, as terminals do.
        Assert.Equal(new OutputSpan(text.IndexOf("warn", StringComparison.Ordinal), 4, Palette(11), null), spans[1]);

        // White on red (fail/crit), 256-color and true color.
        (_, spans) = ansi.Parse("\u001b[1m\u001b[37m\u001b[41mfail\u001b[0m \u001b[38;5;196mx\u001b[0m \u001b[38;2;1;2;3my\u001b[0m");
        Assert.Equal(new OutputSpan(0, 4, Palette(15), Palette(1)), spans[0]);
        Assert.Equal(Color.FromRgb(255, 0, 0), spans[1].Foreground);
        Assert.Equal(Color.FromRgb(1, 2, 3), spans[2].Foreground);
    }

    [Fact]
    public void Drops_other_sequences_and_joins_ones_split_between_chunks()
    {
        var ansi = new AnsiText();
        var (first, firstSpans) = ansi.Parse("a\u001b]0;title\u0007b\u001b[?25lc\u001b[3");
        Assert.Equal("abc", first);
        Assert.Empty(firstSpans);
        var (second, spans) = ansi.Parse("1mred\u001b[0m\r\n");
        Assert.Equal("red\n", second);
        Assert.Equal(new OutputSpan(0, 3, Palette(1), null), Assert.Single(spans));
    }

    [AvaloniaFact]
    public void Output_channel_keeps_runs_aligned_with_its_text()
    {
        var channel = new OutputChannel("Debug");
        channel.Append("> dotnet run");
        channel.AppendRaw("\u001b[32minfo\u001b[39m: started\n");
        var span = Assert.Single(channel.Spans);
        Assert.Equal("info", channel.Document.GetText(span.Start, span.Length));
        Assert.DoesNotContain('\u001b', channel.Document.Text);
        channel.Clear();
        Assert.Empty(channel.Spans);
        Assert.Equal("", channel.Document.Text);
    }
}
