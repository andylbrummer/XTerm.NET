using System.Text;

namespace XTerm;

/// <summary>
/// Feeds raw bytes into a <see cref="Terminal"/>, decoding UTF-8 with state carried across calls.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Terminal.Write(string)"/> takes a string, but the natural source for a terminal is a
/// byte stream — a PTY master, a socket, a subprocess pipe — delivered in fixed-size chunks whose
/// boundaries fall wherever the read happened to end. A multi-byte UTF-8 sequence is routinely
/// split across two chunks, and the obvious
/// <c>terminal.Write(Encoding.UTF8.GetString(chunk))</c> corrupts every one that is: the trailing
/// fragment decodes to U+FFFD, and so does the leading fragment of the next chunk. One box-drawing
/// character in a TUI border, or one CJK character, is enough to see it.
/// </para>
/// <para>
/// <see cref="Decoder"/> exists precisely for this: it retains the incomplete tail between calls
/// and emits the character once the continuation bytes arrive. This type owns one per terminal and
/// keeps the scratch buffer, so callers can hand it chunks without thinking about boundaries.
/// </para>
/// <para>
/// Not thread-safe: the decoder carries state, so one feed serves one producer. That matches the
/// usual arrangement of a single read loop per terminal.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var terminal = new Terminal();
/// var feed = new TerminalByteFeed(terminal);
///
/// var buffer = new byte[4096];
/// int n;
/// while ((n = await ptyStream.ReadAsync(buffer)) > 0)
///     feed.Write(buffer.AsSpan(0, n));
/// </code>
/// </example>
public sealed class TerminalByteFeed
{
    private readonly Terminal _terminal;
    private readonly Decoder _decoder;
    private char[] _chars = new char[1024];

    /// <param name="terminal">The terminal to feed. Not owned; the caller disposes it if needed.</param>
    /// <param name="encoding">
    /// Defaults to UTF-8 without a BOM preamble. Supplying a different encoding is allowed for
    /// legacy streams, but note that terminal control sequences are ASCII in every encoding this
    /// makes sense for.
    /// </param>
    public TerminalByteFeed(Terminal terminal, Encoding? encoding = null)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));

        // throwOnInvalidBytes: false — a terminal must not fall over on a stray byte. Invalid
        // input becomes U+FFFD, which is what a real emulator shows, rather than an exception
        // that would tear down the read loop.
        _decoder = (encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .GetDecoder();
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> and writes the resulting characters to the terminal.
    /// Any incomplete trailing sequence is held until the next call.
    /// </summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        // Worst case is one char per byte for UTF-8, plus room for a carried-over sequence
        // completing into a surrogate pair. GetCharCount would be exact but costs a second pass
        // over the data, and this buffer is reused for the life of the feed.
        var needed = bytes.Length + 2;
        if (_chars.Length < needed)
            _chars = new char[needed];

        var written = _decoder.GetChars(bytes, _chars.AsSpan(), flush: false);
        if (written > 0)
            _terminal.Write(new string(_chars, 0, written));
    }

    /// <summary>
    /// Convenience overload for a byte array segment.
    /// </summary>
    public void Write(byte[] bytes, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(bytes.AsSpan(offset, count));
    }

    /// <summary>
    /// Flushes any incomplete trailing sequence, emitting a replacement character for it.
    ///
    /// <para>
    /// Call at end of stream. Without it a truncated final sequence is simply dropped, which is
    /// usually right for a live terminal but hides genuinely malformed input when processing a
    /// finite capture.
    /// </para>
    /// </summary>
    public void Flush()
    {
        var written = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars.AsSpan(), flush: true);
        if (written > 0)
            _terminal.Write(new string(_chars, 0, written));
    }

    /// <summary>
    /// Discards any carried-over partial sequence. Use when the underlying stream is reset and
    /// the retained bytes no longer belong to what follows.
    /// </summary>
    public void ResetState() => _decoder.Reset();
}
