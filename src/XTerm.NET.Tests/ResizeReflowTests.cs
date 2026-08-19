using Xunit;

namespace XTerm.Tests;

/// <summary>
/// Width-change reflow.
///
/// <para>
/// Before this, <c>Resize</c> resized each physical line independently: narrowing clipped the
/// right-hand side of every long line and widening left previously-wrapped lines broken at the old
/// margin. Resizing a window is an ordinary action and the lost text did not come back, so these
/// tests assert the property that matters — the TEXT survives a width change, and survives a round
/// trip back to the original width.
/// </para>
/// </summary>
public class ResizeReflowTests
{
    private static Terminal Make(int cols, int rows, string? content = null)
    {
        var t = new Terminal();
        t.Resize(cols, rows);
        if (content is not null) t.Write(content);
        return t;
    }

    /// <summary>All visible rows joined, with trailing blanks removed.</summary>
    private static string Visible(Terminal t)
    {
        var rows = new List<string>();
        for (var row = 0; row < t.Rows; row++)
        {
            var index = t.Buffer.YBase + row;
            if (index >= t.Buffer.Lines.Length) break;
            rows.Add(t.Buffer.Lines[index]?.TranslateToString(trimRight: true) ?? "");
        }
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return string.Join("\n", rows);
    }

    /// <summary>Everything on screen with wrapping joined back into logical lines.</summary>
    private static string Logical(Terminal t)
    {
        var text = new System.Text.StringBuilder();
        for (var row = 0; row < t.Rows; row++)
        {
            var index = t.Buffer.YBase + row;
            if (index >= t.Buffer.Lines.Length) break;
            var line = t.Buffer.Lines[index];
            if (line is null) continue;

            if (row > 0 && !line.IsWrapped) text.Append('\n');
            text.Append(line.TranslateToString(trimRight: true));
        }
        return text.ToString().TrimEnd('\n');
    }

    [Fact]
    public void Narrowing_DoesNotClipALongLine()
    {
        // 60 characters at width 60: exactly one row, no wrapping yet.
        var content = new string('x', 60);
        var t = Make(60, 10, content);

        t.Resize(30, 10);

        // The old behaviour truncated each line to the new width, losing half the text.
        Assert.Equal(content, Logical(t).Replace("\n", ""));
    }

    [Fact]
    public void Widening_RejoinsAPreviouslyWrappedLine()
    {
        var content = new string('y', 60);
        var t = Make(30, 10, content); // wraps across two rows

        t.Resize(60, 10);

        // Back on one row, not left broken at the old margin.
        Assert.Equal(content, Visible(t));
    }

    [Fact]
    public void RoundTripThroughANarrowerWidth_PreservesText()
    {
        var content = "The quick brown fox jumps over the lazy dog and keeps running for a while";
        var t = Make(80, 10, content);

        t.Resize(20, 10);
        t.Resize(80, 10);

        Assert.Equal(content, Logical(t));
    }

    [Fact]
    public void SeparateLines_AreNotMergedByReflow()
    {
        var t = Make(40, 10, "first\r\nsecond\r\nthird");

        t.Resize(20, 10);

        Assert.Equal("first\nsecond\nthird", Logical(t));
    }

    [Fact]
    public void BlankLinesBetweenContent_AreKept()
    {
        var t = Make(40, 10, "a\r\n\r\nb");

        t.Resize(20, 10);

        Assert.Equal("a\n\nb", Logical(t));
    }

    [Fact]
    public void Attributes_SurviveReflow()
    {
        var t = Make(40, 10, "\x1b[38;2;10;20;30m" + new string('z', 50) + "\x1b[0m");

        t.Resize(20, 10);

        // Find the first cell of the reflowed content and check its colour came along.
        var cell = t.Buffer.Lines[t.Buffer.YBase]![0];
        Assert.Equal(1, cell.Attributes.GetFgColorMode());
        var rgb = cell.Attributes.GetFgColor();
        Assert.Equal(10, (rgb >> 16) & 0xFF);
        Assert.Equal(20, (rgb >> 8) & 0xFF);
        Assert.Equal(30, rgb & 0xFF);
    }

    [Fact]
    public void Hyperlinks_SurviveReflow()
    {
        var t = Make(40, 10, "\x1b]8;;https://example.com\x1b\\" + new string('q', 50) + "\x1b]8;;\x1b\\");

        t.Resize(20, 10);

        Assert.Equal("https://example.com", t.Buffer.Lines[t.Buffer.YBase]![0].Hyperlink);
    }

    [Fact]
    public void CursorFollowsItsCharacter_WhenNarrowing()
    {
        // Cursor sits right after 50 characters; at width 20 that is row 2, column 10.
        var t = Make(40, 10, new string('c', 50));

        t.Resize(20, 10);

        var absolute = t.Buffer.YBase + t.Buffer.Y;
        var offset = absolute * 20 + t.Buffer.X;
        var firstRow = t.Buffer.YBase * 20;
        Assert.Equal(50, offset - firstRow);
    }

    [Fact]
    public void CursorStaysWithinTheViewport_AfterReflow()
    {
        var t = Make(80, 10, string.Join("", Enumerable.Range(0, 40).Select(i => $"line {i}\r\n")));

        t.Resize(20, 10);

        Assert.InRange(t.Buffer.Y, 0, t.Rows - 1);
        Assert.InRange(t.Buffer.X, 0, t.Cols - 1);
    }

    [Fact]
    public void HeightOnlyChange_DoesNotDisturbText()
    {
        var t = Make(40, 10, "alpha\r\nbeta\r\ngamma");
        var before = Logical(t);

        t.Resize(40, 20);

        Assert.Equal(before, Logical(t));
    }

    /// <summary>
    /// The alternate buffer belongs to a full-screen program that repaints on SIGWINCH.
    /// Reflowing it would show a frame the program never drew.
    /// </summary>
    [Fact]
    public void AlternateBuffer_IsNotReflowed()
    {
        var t = Make(40, 10);
        t.Write("\x1b[?1049h");           // enter alt screen
        t.Write(new string('a', 35));

        t.Resize(20, 10);

        Assert.True(t.IsAlternateBufferActive);
        // Clipped, not rewrapped: no second row was created for the overflow.
        var secondRow = t.Buffer.Lines[t.Buffer.YBase + 1];
        Assert.True(secondRow is null || secondRow.GetTrimmedLength() == 0,
            "alt buffer content must not be rewrapped onto another row");
    }

    [Fact]
    public void ReflowThenSerialize_StillRoundTrips()
    {
        // Reflow and serialization have to agree about IsWrapped, or a resized screen replays
        // with its line breaks in the wrong places.
        var content = "wrapped content that is quite long and will span rows at narrow widths";
        var source = Make(80, 10, content);
        source.Resize(24, 10);

        var replayed = new Terminal();
        replayed.Resize(24, 10);
        replayed.Write(TerminalSerializer.Serialize(source));

        Assert.Equal(Logical(source), Logical(replayed));
    }

    [Fact]
    public void EmptyBuffer_ResizesWithoutError()
    {
        var t = Make(40, 10);
        t.Resize(20, 5);
        Assert.Equal(20, t.Cols);
        Assert.Equal(5, t.Rows);
    }
}
