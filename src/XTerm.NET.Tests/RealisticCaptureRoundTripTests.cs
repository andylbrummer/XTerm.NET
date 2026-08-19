using System.Text;
using XTerm.Buffer;
using Xunit;
using Xunit.Abstractions;

namespace XTerm.Tests;

/// <summary>
/// End-to-end fidelity: feed a dense, realistic TUI capture through
/// <see cref="TerminalByteFeed"/> in awkward chunks, serialize it, replay that into a second
/// terminal, and compare every cell.
///
/// <para>
/// The unit tests elsewhere prove the cases someone thought of. This one exists for the cases
/// nobody would write by hand: dense 24-bit colour runs where fg and bg change on every cell,
/// wide characters adjacent to box drawing, an alternate-screen program that hid its cursor and
/// left input modes set — the combinations a real full-screen CLI produces. It is also the
/// closest automated stand-in for "does a reattached session actually look right".
/// </para>
/// </summary>
public class RealisticCaptureRoundTripTests
{
    private readonly ITestOutputHelper _out;

    public RealisticCaptureRoundTripTests(ITestOutputHelper output) => _out = output;

    private const int Cols = 80;
    private const int Rows = 24;

    /// <summary>
    /// A capture shaped like a modern CLI's full-screen output: alt screen, hidden cursor, a
    /// 24-bit gradient changing colour every cell, box drawing around wide characters, mixed
    /// attribute runs, and input modes left set at the end.
    /// </summary>
    private static byte[] BuildCapture()
    {
        var sb = new StringBuilder();

        sb.Append("\x1b[?1049h");   // enter alternate screen
        sb.Append("\x1b[?25l");     // hide cursor
        sb.Append("\x1b[2J\x1b[H"); // clear + home

        sb.Append("\x1b[1m Claude Code \x1b[0m\r\n");

        // Gradient: both fg and bg true-colour, changing every single cell. This is the run that
        // a palette-downgrading serializer would round-trip as text and ruin as colour.
        for (var i = 0; i < 60; i++)
        {
            var r = 255 - i * 4;
            var g = i * 4;
            const int b = 128;
            sb.Append($"\x1b[38;2;{r};{g};{b}m\x1b[48;2;{b};{r};{g}m#");
        }
        sb.Append("\x1b[0m\r\n");

        sb.Append("┌────────────────────────┐\r\n");
        sb.Append("│ 日本語 and emoji 🎉 ok │\r\n");
        sb.Append("└────────────────────────┘\r\n");

        sb.Append("\x1b[1;38;2;255;200;0mbold-truecolor\x1b[0m ");
        sb.Append("\x1b[3;4;38;5;208mitalic-underline-256\x1b[0m ");
        sb.Append("\x1b[7minverse\x1b[0m\r\n");

        sb.Append("\x1b[?1h\x1b[?2004h"); // app cursor keys + bracketed paste
        sb.Append("\x1b[5;3H");           // park the cursor mid-screen

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    public void RealisticCapture_SurvivesChunkedFeedSerializeAndReplay()
    {
        var capture = BuildCapture();

        var source = new Terminal();
        source.Resize(Cols, Rows);
        var feed = new TerminalByteFeed(source);

        // 7-byte chunks: deliberately awkward, tearing UTF-8 and escape sequences at boundaries
        // the way a real 4KB-read loop tears them at arbitrary offsets.
        for (var i = 0; i < capture.Length; i += 7)
            feed.Write(capture.AsSpan(i, Math.Min(7, capture.Length - i)));
        feed.Flush();

        var serialized = TerminalSerializer.Serialize(source);

        var replayed = new Terminal();
        replayed.Resize(Cols, Rows);
        replayed.Write(serialized);

        var problems = Compare(source, replayed, out var cells);

        _out.WriteLine($"capture    : {capture.Length} bytes");
        _out.WriteLine($"serialized : {Encoding.UTF8.GetByteCount(serialized)} bytes");
        _out.WriteLine($"cells      : {cells}");
        foreach (var p in problems.Take(20)) _out.WriteLine("  " + p);

        Assert.Empty(problems);

        // State that is invisible in the cells but decides how the session behaves.
        Assert.True(replayed.IsAlternateBufferActive);
        Assert.False(replayed.CursorVisible);
        Assert.True(replayed.ApplicationCursorKeys);
        Assert.True(replayed.BracketedPasteMode);
        Assert.Equal(source.Buffer.X, replayed.Buffer.X);
        Assert.Equal(source.Buffer.Y, replayed.Buffer.Y);
    }

    /// <summary>
    /// The serialized form should be a screen, not a transcript: bounded by the terminal's size
    /// rather than by how much the program has written.
    /// </summary>
    [Fact]
    public void SerializedSize_ScalesWithScreenNotHistory()
    {
        var terminal = new Terminal();
        terminal.Resize(Cols, Rows);

        // Write far more than one screen: 2000 lines of scrolling output.
        for (var i = 0; i < 2000; i++)
            terminal.Write($"line {i} with some padding text to make it wider\r\n");

        var serialized = TerminalSerializer.Serialize(terminal);
        var bytes = Encoding.UTF8.GetByteCount(serialized);

        _out.WriteLine($"after 2000 lines, serialized screen = {bytes} bytes");

        // A full screen of text with attributes, generously bounded. The point is that this does
        // not grow with the 2000 lines behind it.
        Assert.True(bytes < Cols * Rows * 8,
            $"serialized output ({bytes}B) should be screen-sized, not history-sized");
    }

    private static List<string> Compare(Terminal a, Terminal b, out int cellsChecked)
    {
        var problems = new List<string>();
        cellsChecked = 0;

        for (var row = 0; row < a.Rows; row++)
        {
            var la = LineAt(a, row);
            var lb = LineAt(b, row);

            if (la is null || lb is null)
            {
                if (la is not null || lb is not null)
                    problems.Add($"row {row}: one side has no line");
                continue;
            }

            var len = Math.Max(la.GetTrimmedLength(), lb.GetTrimmedLength());
            for (var col = 0; col < len; col++)
            {
                cellsChecked++;
                var ca = la[col];
                var cb = lb[col];

                if (ca.Content != cb.Content)
                {
                    problems.Add($"r{row}c{col} content '{ca.Content}' != '{cb.Content}'");
                }
                else if (!ca.Attributes.Equals(cb.Attributes))
                {
                    problems.Add(
                        $"r{row}c{col} fg {ca.Attributes.GetFgColorMode()}/{ca.Attributes.GetFgColor()}" +
                        $" != {cb.Attributes.GetFgColorMode()}/{cb.Attributes.GetFgColor()}" +
                        $" bg {ca.Attributes.GetBgColorMode()}/{ca.Attributes.GetBgColor()}" +
                        $" != {cb.Attributes.GetBgColorMode()}/{cb.Attributes.GetBgColor()}");
                }
            }
        }

        return problems;
    }

    private static BufferLine? LineAt(Terminal t, int row)
    {
        var index = t.Buffer.YBase + row;
        return index < t.Buffer.Lines.Length ? t.Buffer.Lines[index] : null;
    }
}
