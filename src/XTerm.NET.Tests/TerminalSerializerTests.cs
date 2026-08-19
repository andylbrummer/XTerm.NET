using Xunit;

namespace XTerm.Tests;

/// <summary>
/// Round-trip tests for <see cref="TerminalSerializer"/>.
///
/// <para>
/// The property under test is the one that makes the serializer useful: feeding a terminal, then
/// feeding a SECOND terminal the serialized form of the first, must produce the same screen. Every
/// test here asserts that equivalence rather than asserting on the escape sequences themselves —
/// the exact bytes are an implementation detail, and pinning them would make every optimisation a
/// test change.
/// </para>
/// </summary>
public class TerminalSerializerTests
{
    private static Terminal Feed(string data, int cols = 40, int rows = 10)
    {
        var t = new Terminal();
        t.Resize(cols, rows);
        t.Write(data);
        return t;
    }

    /// <summary>Replays a serialized terminal into a fresh one of the same size.</summary>
    private static Terminal RoundTrip(Terminal source, TerminalSerializeOptions? options = null)
    {
        var serialized = TerminalSerializer.Serialize(source, options);
        var replayed = new Terminal();
        replayed.Resize(source.Cols, source.Rows);
        replayed.Write(serialized);
        return replayed;
    }

    private static string ScreenText(Terminal t)
    {
        var lines = new List<string>();
        for (var row = 0; row < t.Rows; row++)
        {
            var index = t.Buffer.YBase + row;
            if (index >= t.Buffer.Lines.Length) break;
            var line = t.Buffer.Lines[index];
            lines.Add(line is null ? "" : line.TranslateToString(trimRight: true));
        }
        return string.Join("\n", lines).TrimEnd('\n');
    }

    [Fact]
    public void PlainText_RoundTrips()
    {
        var source = Feed("hello\r\nworld");
        Assert.Equal(ScreenText(source), ScreenText(RoundTrip(source)));
    }

    [Fact]
    public void CursorPosition_IsRestored()
    {
        var source = Feed("abc\r\ndef[1;2H");
        var replayed = RoundTrip(source);

        Assert.Equal(source.Buffer.X, replayed.Buffer.X);
        Assert.Equal(source.Buffer.Y, replayed.Buffer.Y);
    }

    [Fact]
    public void BasicColors_RoundTrip()
    {
        var source = Feed("[31mred[32mgreen[0mplain");
        var replayed = RoundTrip(source);

        Assert.Equal(ScreenText(source), ScreenText(replayed));
        AssertRowAttributesMatch(source, replayed, row: 0);
    }

    [Fact]
    public void BrightColors_RoundTrip()
    {
        var source = Feed("[91mbright[101mbg[0m");
        AssertRowAttributesMatch(source, RoundTrip(source), row: 0);
    }

    [Fact]
    public void Palette256_RoundTrips()
    {
        var source = Feed("[38;5;208morange[48;5;27mbg[0m");
        AssertRowAttributesMatch(source, RoundTrip(source), row: 0);
    }

    /// <summary>
    /// 24-bit colour, which is what modern CLIs actually emit — Claude Code's TUI among them.
    /// A serializer that quietly downgraded RGB to the nearest palette entry would round-trip
    /// the TEXT perfectly and lose the thing the user notices.
    /// </summary>
    [Fact]
    public void TrueColor_RoundTripsExactly()
    {
        var source = Feed("[38;2;255;128;64mfg[48;2;12;34;56mbg[0m");
        var replayed = RoundTrip(source);

        AssertRowAttributesMatch(source, replayed, row: 0);

        // Assert the exact channel values survived, not merely that the two agree.
        var cell = replayed.Buffer.Lines[replayed.Buffer.YBase]![0];
        Assert.Equal(1, cell.Attributes.GetFgColorMode());
        var rgb = cell.Attributes.GetFgColor();
        Assert.Equal(255, (rgb >> 16) & 0xFF);
        Assert.Equal(128, (rgb >> 8) & 0xFF);
        Assert.Equal(64, rgb & 0xFF);
    }

    [Fact]
    public void TrueColorBackground_RoundTripsExactly()
    {
        var source = Feed("[48;2;1;2;3mx[0m");
        var replayed = RoundTrip(source);

        var cell = replayed.Buffer.Lines[replayed.Buffer.YBase]![0];
        Assert.Equal(1, cell.Attributes.GetBgColorMode());
        var rgb = cell.Attributes.GetBgColor();
        Assert.Equal(1, (rgb >> 16) & 0xFF);
        Assert.Equal(2, (rgb >> 8) & 0xFF);
        Assert.Equal(3, rgb & 0xFF);
    }

    /// <summary>
    /// A true-colour run adjacent to a default-colour run. Guards the sentinel handling: palette
    /// 256/257 mean "default", and treating them as ordinary indices paints colour 0 (black) —
    /// invisible on a dark theme, glaring on a light one.
    /// </summary>
    [Fact]
    public void TrueColorFollowedByDefault_KeepsDefaultDefault()
    {
        var source = Feed("[38;2;200;100;50mcolored[39mdefault");
        var replayed = RoundTrip(source);

        var line = replayed.Buffer.Lines[replayed.Buffer.YBase]!;
        var defaultCell = line[7]; // first cell after "colored"
        Assert.Equal(256, defaultCell.Attributes.GetFgColor());
        AssertRowAttributesMatch(source, replayed, row: 0);
    }

    [Fact]
    public void TextAttributes_RoundTrip()
    {
        var source = Feed("[1mbold[3mitalic[4munderline[9mstrike[0m");
        AssertRowAttributesMatch(source, RoundTrip(source), row: 0);
    }

    [Fact]
    public void AlternateBuffer_IsReEnteredBeforeContent()
    {
        var source = Feed("normal[?1049halt-screen");

        Assert.True(source.IsAlternateBufferActive);

        var replayed = RoundTrip(source);
        Assert.True(replayed.IsAlternateBufferActive);
        Assert.Contains("alt-screen", ScreenText(replayed), StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenCursor_StaysHidden()
    {
        var source = Feed("[?25l");
        Assert.False(RoundTrip(source).CursorVisible);
    }

    [Fact]
    public void VisibleCursor_StaysVisible()
    {
        var source = Feed("[?25ltext[?25h");
        Assert.True(RoundTrip(source).CursorVisible);
    }

    /// <summary>
    /// Modes are invisible in a repaint but change how the session BEHAVES. A restored screen
    /// without bracketed paste turns a paste into keystrokes; without application cursor keys,
    /// arrows stop working in the full-screen programs this feature exists for.
    /// </summary>
    [Fact]
    public void InputModes_RoundTrip()
    {
        var source = Feed("[?1h[?2004h");
        var replayed = RoundTrip(source);

        Assert.True(replayed.ApplicationCursorKeys);
        Assert.True(replayed.BracketedPasteMode);
    }

    [Fact]
    public void Modes_CanBeSuppressed()
    {
        var source = Feed("[?2004h");
        var replayed = RoundTrip(source, new TerminalSerializeOptions { IncludeModes = false });

        Assert.False(replayed.BracketedPasteMode);
    }

    [Fact]
    public void Scrollback_IsExcludedByDefault()
    {
        var source = Feed(string.Join("", Enumerable.Range(0, 30).Select(i => $"line{i}\r\n")), rows: 5);
        var replayed = RoundTrip(source);

        // Only the visible screen: the earliest lines scrolled off and were not requested.
        Assert.DoesNotContain("line0", ScreenText(replayed), StringComparison.Ordinal);
    }

    [Fact]
    public void Scrollback_IsIncludedWhenRequested()
    {
        var source = Feed(string.Join("", Enumerable.Range(0, 30).Select(i => $"line{i}\r\n")), rows: 5);
        var replayed = RoundTrip(source, new TerminalSerializeOptions { ScrollbackLines = 10 });

        // The visible screen still ends where the source's did.
        Assert.Equal(ScreenText(source), ScreenText(replayed));

        // And the requested history is above it rather than lost.
        Assert.True(replayed.Buffer.YBase > 0, "scrollback should have been produced");
    }

    [Fact]
    public void ScrollbackRequest_BeyondAvailable_IsClamped()
    {
        var source = Feed("just one line");
        var replayed = RoundTrip(source, new TerminalSerializeOptions { ScrollbackLines = 5000 });

        Assert.Equal(ScreenText(source), ScreenText(replayed));
    }

    [Fact]
    public void WideCharacters_RoundTrip()
    {
        // CJK: two columns per glyph. A serializer that emitted the zero-width continuation cell
        // would shift the rest of the line by one column per wide character.
        var source = Feed("日本語text");
        Assert.Equal(ScreenText(source), ScreenText(RoundTrip(source)));
    }

    [Fact]
    public void BoxDrawing_RoundTrips()
    {
        var source = Feed("┌───┐\r\n│ x │\r\n└───┘");
        Assert.Equal(ScreenText(source), ScreenText(RoundTrip(source)));
    }

    [Fact]
    public void EmptyTerminal_ProducesReplayableOutput()
    {
        var source = Feed("");
        var replayed = RoundTrip(source);

        Assert.Equal(ScreenText(source), ScreenText(replayed));
        Assert.Equal(0, replayed.Buffer.X);
        Assert.Equal(0, replayed.Buffer.Y);
    }

    [Fact]
    public void Title_RoundTrips()
    {
        var source = Feed("]0;my-title");
        Assert.Equal("my-title", RoundTrip(source).Title);
    }

    /// <summary>
    /// Serializing must not mutate the terminal it reads — it runs on a live session while the
    /// program behind it keeps writing.
    /// </summary>
    [Fact]
    public void Serialize_DoesNotMutateSource()
    {
        var source = Feed("[31mcolored[0m\r\nsecond");
        var before = ScreenText(source);
        var (x, y) = (source.Buffer.X, source.Buffer.Y);

        TerminalSerializer.Serialize(source);

        Assert.Equal(before, ScreenText(source));
        Assert.Equal(x, source.Buffer.X);
        Assert.Equal(y, source.Buffer.Y);
    }

    /// <summary>Compares every cell's attributes on one row between two terminals.</summary>
    private static void AssertRowAttributesMatch(Terminal expected, Terminal actual, int row)
    {
        var expectedLine = expected.Buffer.Lines[expected.Buffer.YBase + row];
        var actualLine = actual.Buffer.Lines[actual.Buffer.YBase + row];

        Assert.NotNull(expectedLine);
        Assert.NotNull(actualLine);

        var length = expectedLine!.GetTrimmedLength();
        for (var col = 0; col < length; col++)
        {
            var e = expectedLine[col];
            var a = actualLine![col];
            Assert.True(
                e.Attributes.Equals(a.Attributes),
                $"attributes differ at column {col}: " +
                $"fg {e.Attributes.GetFgColorMode()}/{e.Attributes.GetFgColor()} vs " +
                $"{a.Attributes.GetFgColorMode()}/{a.Attributes.GetFgColor()}, " +
                $"bg {e.Attributes.GetBgColorMode()}/{e.Attributes.GetBgColor()} vs " +
                $"{a.Attributes.GetBgColorMode()}/{a.Attributes.GetBgColor()}");
        }
    }
}
