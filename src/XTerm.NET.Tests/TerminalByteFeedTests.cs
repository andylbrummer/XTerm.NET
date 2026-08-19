using System.Text;
using Xunit;

namespace XTerm.Tests;

/// <summary>
/// Tests for <see cref="TerminalByteFeed"/>.
///
/// <para>
/// The case that matters is a multi-byte UTF-8 sequence split across two writes. A PTY read loop
/// hands out fixed-size chunks whose boundaries fall wherever the read ended, so this is the
/// normal case, not an edge one — and the naive
/// <c>terminal.Write(Encoding.UTF8.GetString(chunk))</c> corrupts every occurrence into a pair of
/// replacement characters. One box-drawing character in a TUI border is enough to see it.
/// </para>
/// </summary>
public class TerminalByteFeedTests
{
    private static (Terminal Terminal, TerminalByteFeed Feed) Create(int cols = 40, int rows = 10)
    {
        var t = new Terminal();
        t.Resize(cols, rows);
        return (t, new TerminalByteFeed(t));
    }

    private static string FirstLine(Terminal t)
    {
        var line = t.Buffer.Lines[t.Buffer.YBase];
        return line is null ? "" : line.TranslateToString(trimRight: true);
    }

    [Fact]
    public void Ascii_IsWritten()
    {
        var (t, feed) = Create();
        feed.Write("hello"u8);
        Assert.Equal("hello", FirstLine(t));
    }

    [Fact]
    public void MultiByteCharacter_SplitAcrossWrites_IsNotCorrupted()
    {
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("é"); // 2 bytes

        feed.Write(bytes.AsSpan(0, 1));
        Assert.Equal("", FirstLine(t)); // nothing emitted yet — the character is incomplete

        feed.Write(bytes.AsSpan(1));
        Assert.Equal("é", FirstLine(t));
    }

    [Fact]
    public void ThreeByteCharacter_SplitAtEveryBoundary_IsNotCorrupted()
    {
        // Box drawing: exactly what a TUI border is made of, and 3 bytes in UTF-8.
        var bytes = Encoding.UTF8.GetBytes("─");
        Assert.Equal(3, bytes.Length);

        for (var split = 1; split < bytes.Length; split++)
        {
            var (t, feed) = Create();
            feed.Write(bytes.AsSpan(0, split));
            feed.Write(bytes.AsSpan(split));
            Assert.Equal("─", FirstLine(t));
        }
    }

    [Fact]
    public void FourByteCharacter_SplitAtEveryBoundary_IsNotCorrupted()
    {
        // Emoji: 4 bytes, and a surrogate pair once decoded — the case where the output char
        // count exceeds what a naive per-byte buffer estimate would allow for.
        var bytes = Encoding.UTF8.GetBytes("🎉");
        Assert.Equal(4, bytes.Length);

        for (var split = 1; split < bytes.Length; split++)
        {
            var (t, feed) = Create();
            feed.Write(bytes.AsSpan(0, split));
            feed.Write(bytes.AsSpan(split));
            Assert.Equal("🎉", FirstLine(t));
        }
    }

    [Fact]
    public void EscapeSequence_SplitAcrossWrites_StillApplies()
    {
        // The parser has its own cross-call state; this asserts the byte layer does not disturb
        // it. A colour sequence torn in half must still colour the text that follows.
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("[31mred");

        feed.Write(bytes.AsSpan(0, 2));
        feed.Write(bytes.AsSpan(2));

        Assert.Equal("red", FirstLine(t));
        var cell = t.Buffer.Lines[t.Buffer.YBase]![0];
        Assert.Equal(1, cell.Attributes.GetFgColor()); // SGR 31 -> palette index 1
    }

    [Fact]
    public void TrueColorSequence_SplitAcrossManyWrites_SurvivesIntact()
    {
        // A 24-bit colour sequence is long enough to be split more than once by a small read
        // buffer, which is the realistic case for a CLI that emits them densely.
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("[38;2;255;128;64mx");

        for (var i = 0; i < bytes.Length; i++)
            feed.Write(bytes.AsSpan(i, 1)); // one byte at a time — the worst case

        var cell = t.Buffer.Lines[t.Buffer.YBase]![0];
        Assert.Equal(1, cell.Attributes.GetFgColorMode());
        var rgb = cell.Attributes.GetFgColor();
        Assert.Equal(255, (rgb >> 16) & 0xFF);
        Assert.Equal(128, (rgb >> 8) & 0xFF);
        Assert.Equal(64, rgb & 0xFF);
    }

    [Fact]
    public void LongMixedContent_ChunkedArbitrarily_MatchesSingleWrite()
    {
        // The property that actually matters: chunking must be invisible. Compare a terminal fed
        // in awkward 7-byte slices against one fed the whole string at once.
        var content = "┌─ header ─┐\r\n│ 日本語 ok │\r\n└──────────┘\r\n🎉 done";

        var (chunked, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        for (var i = 0; i < bytes.Length; i += 7)
            feed.Write(bytes.AsSpan(i, Math.Min(7, bytes.Length - i)));

        var (whole, _) = Create();
        whole.Write(content);

        for (var row = 0; row < whole.Rows; row++)
        {
            var a = chunked.Buffer.Lines[chunked.Buffer.YBase + row]?.TranslateToString(true) ?? "";
            var b = whole.Buffer.Lines[whole.Buffer.YBase + row]?.TranslateToString(true) ?? "";
            Assert.Equal(b, a);
        }
    }

    [Fact]
    public void InvalidBytes_ProduceReplacementRatherThanThrowing()
    {
        // A stray byte must not tear down the read loop that feeds a live terminal.
        var (t, feed) = Create();
        feed.Write(new byte[] { 0xFF, 0xFE });
        feed.Flush();

        Assert.Contains('�', FirstLine(t));
    }

    [Fact]
    public void EmptyWrite_IsIgnored()
    {
        var (t, feed) = Create();
        feed.Write(ReadOnlySpan<byte>.Empty);
        Assert.Equal("", FirstLine(t));
    }

    [Fact]
    public void Flush_EmitsReplacementForTruncatedTail()
    {
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("é");

        feed.Write(bytes.AsSpan(0, 1)); // incomplete, held
        Assert.Equal("", FirstLine(t));

        feed.Flush(); // end of stream: the tail is malformed after all
        Assert.Contains('�', FirstLine(t));
    }

    [Fact]
    public void ResetState_DiscardsThePartialSequence()
    {
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("é");

        feed.Write(bytes.AsSpan(0, 1));
        feed.ResetState();
        feed.Write("ok"u8);

        // The orphaned lead byte is gone rather than combining with what follows.
        Assert.Equal("ok", FirstLine(t));
    }

    [Fact]
    public void ArrayOverload_MatchesSpanOverload()
    {
        var (t, feed) = Create();
        var bytes = Encoding.UTF8.GetBytes("xhello");
        feed.Write(bytes, 1, bytes.Length - 1);
        Assert.Equal("hello", FirstLine(t));
    }

    [Fact]
    public void LargeChunk_GrowsTheScratchBufferWithoutLoss()
    {
        // Exceeds the initial 1024-char scratch buffer, exercising the growth path.
        var (t, feed) = Create(cols: 200, rows: 50);
        var content = new string('a', 5000);
        feed.Write(Encoding.UTF8.GetBytes(content));

        Assert.Equal(new string('a', 200), FirstLine(t));
    }

    [Fact]
    public void NullTerminal_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminalByteFeed(null!));
    }
}
