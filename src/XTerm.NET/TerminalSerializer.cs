using System.Text;
using XTerm.Buffer;

namespace XTerm;

/// <summary>
/// Options for <see cref="TerminalSerializer"/>.
/// </summary>
public sealed class TerminalSerializeOptions
{
    /// <summary>
    /// How many scrollback lines to include above the visible screen. 0 serializes the screen
    /// only. Capped at the buffer's actual scrollback.
    /// </summary>
    public int ScrollbackLines { get; set; }

    /// <summary>
    /// Emit the modes that survive a repaint but are invisible in it — cursor visibility,
    /// application cursor keys, bracketed paste, origin mode, reverse video.
    ///
    /// <para>
    /// On by default, and it matters more than it looks: a screen restored without them renders
    /// correctly and then behaves wrongly. Bracketed paste missing means a paste is interpreted
    /// as keystrokes; application-cursor-keys missing means arrow keys stop working in the very
    /// full-screen programs this is for.
    /// </para>
    /// </summary>
    public bool IncludeModes { get; set; } = true;

    /// <summary>Emit an OSC 0 title sequence when the terminal has a title.</summary>
    public bool IncludeTitle { get; set; } = true;
}

/// <summary>
/// Renders a <see cref="Terminal"/>'s current state as a string of escape sequences that
/// reproduces it when written to another terminal.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart to feeding a terminal: it answers "what does this look like now" in a
/// form any terminal can consume. It is what a detach/reattach model needs. Replaying the original
/// byte stream is not equivalent — it costs time proportional to history rather than to the
/// screen, it can begin mid-escape-sequence if the history was trimmed, and for a full-screen
/// program the history is not the state at all. tmux takes the same approach: it keeps a screen
/// and paints it on attach rather than replaying what the program once wrote.
/// </para>
/// <para>
/// The output is deliberately plain: SGR, cursor positioning, and the handful of DEC private modes
/// that outlive a repaint. No terminfo, no capability negotiation — every sequence used here is
/// understood by xterm, and by xterm.js.
/// </para>
/// <para>
/// Modelled on xterm.js's SerializeAddon, and the same caveats apply: this reproduces what the
/// terminal <em>shows</em>, not the program's own notion of state. A program that tracks its
/// cursor independently may still want a repaint trigger.
/// </para>
/// </remarks>
public static class TerminalSerializer
{
    private const string Esc = "";
    private const string Csi = Esc + "[";

    /// <summary>
    /// Serializes <paramref name="terminal"/> to a replayable escape-sequence string.
    /// </summary>
    public static string Serialize(Terminal terminal, TerminalSerializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        options ??= new TerminalSerializeOptions();

        var sb = new StringBuilder(terminal.Cols * terminal.Rows * 2);

        // Start from a known state. Without the reset, leftover attributes on the receiving
        // terminal bleed into the first cells we write.
        sb.Append(Csi).Append("0m");

        // The alternate buffer must be entered BEFORE its content is written, or the content
        // lands in the normal buffer and is then hidden by the switch. 1049 is the composite
        // (save cursor + switch + clear) that every modern full-screen program uses.
        if (terminal.IsAlternateBufferActive)
            sb.Append(Csi).Append("?1049h");

        SerializeBuffer(terminal, sb, options);

        if (options.IncludeModes)
            SerializeModes(terminal, sb);

        if (options.IncludeTitle && !string.IsNullOrEmpty(terminal.Title))
        {
            // OSC 0 sets both icon name and window title, terminated with BEL for the widest
            // acceptance (ST is correct but less universally handled).
            sb.Append(Esc).Append("]0;").Append(terminal.Title).Append('');
        }

        return sb.ToString();
    }

    private static void SerializeBuffer(Terminal terminal, StringBuilder sb, TerminalSerializeOptions options)
    {
        var buffer = terminal.Buffer;
        var lines = buffer.Lines;

        // YBase is the first visible row's index in the line list; everything above it is
        // scrollback. Clamp the request to what actually exists.
        var scrollback = Math.Clamp(options.ScrollbackLines, 0, buffer.YBase);
        var firstRow = buffer.YBase - scrollback;
        var lastRow = buffer.YBase + terminal.Rows - 1;

        // Never read past the end: a buffer that has not yet filled its rows has fewer lines
        // than YBase + Rows.
        if (lastRow >= lines.Length)
            lastRow = lines.Length - 1;

        var current = AttributeData.Default;
        var firstLine = true;

        for (var row = firstRow; row <= lastRow; row++)
        {
            var line = lines[row];
            if (line is null)
                continue;

            // A wrapped line is the continuation of the one above: the terminal produced it by
            // running past the right margin, and it must be reproduced the same way. Emitting a
            // newline here would turn one logical line into two, which is visible the moment the
            // receiving terminal is a different width.
            if (!firstLine && !line.IsWrapped)
                sb.Append("\r\n");
            firstLine = false;

            WriteLineCells(line, sb, ref current);
        }

        // Reset attributes before positioning so the cursor does not inherit the last cell's
        // colours for whatever the program writes next.
        if (!current.Equals(AttributeData.Default))
        {
            sb.Append(Csi).Append("0m");
            current = AttributeData.Default;
        }

        // CUP is 1-based. Y is relative to the visible screen, which is what we just painted.
        sb.Append(Csi).Append(buffer.Y + 1).Append(';').Append(buffer.X + 1).Append('H');
    }

    private static void WriteLineCells(BufferLine line, StringBuilder sb, ref AttributeData current)
    {
        // Trailing blanks carry no information — the receiving terminal is already blank there —
        // and emitting them would be most of the payload for a typical screen.
        var length = line.GetTrimmedLength();

        for (var col = 0; col < length; col++)
        {
            var cell = line[col];

            // The second half of a wide character occupies a cell but must not be written; the
            // terminal advances two columns when it renders the first half.
            if (cell.Width == 0)
                continue;

            if (!cell.Attributes.Equals(current))
            {
                AppendSgr(sb, cell.Attributes);
                current = cell.Attributes;
            }

            sb.Append(cell.Content.Length > 0 ? cell.Content : " ");
        }
    }

    /// <summary>
    /// Emits a full SGR sequence for <paramref name="attr"/>.
    /// </summary>
    /// <remarks>
    /// Absolute, not a diff against the previous attributes: it opens with a reset. Diffing would
    /// produce shorter output but has to get every "turn this off" code right (and several have
    /// no reliable single-attribute reset across terminals). For a screen-sized payload the
    /// saving is not worth the class of bug it invites.
    /// </remarks>
    private static void AppendSgr(StringBuilder sb, AttributeData attr)
    {
        sb.Append(Csi).Append('0');

        if (attr.IsBold()) sb.Append(";1");
        if (attr.IsDim()) sb.Append(";2");
        if (attr.IsItalic()) sb.Append(";3");
        if (attr.IsUnderline()) sb.Append(";4");
        if (attr.IsBlink()) sb.Append(";5");
        if (attr.IsInverse()) sb.Append(";7");
        if (attr.IsInvisible()) sb.Append(";8");
        if (attr.IsStrikethrough()) sb.Append(";9");
        if (attr.IsOverline()) sb.Append(";53");

        AppendColor(sb, attr.GetFgColorMode(), attr.GetFgColor(), isForeground: true);
        AppendColor(sb, attr.GetBgColorMode(), attr.GetBgColor(), isForeground: false);

        sb.Append('m');
    }

    /// <summary>
    /// Appends the colour parameters for one channel.
    /// </summary>
    /// <remarks>
    /// Mirrors how InputHandler stores colours: mode 0 is a palette index where 256 means
    /// "default foreground" and 257 means "default background" (the sentinels SGR 39 / 49 set),
    /// and mode 1 is 24-bit RGB packed as 0xRRGGBB. Getting these sentinels wrong is invisible on
    /// a dark theme and glaring on a light one, because "default" silently becomes colour 0.
    /// </remarks>
    private static void AppendColor(StringBuilder sb, int mode, int color, bool isForeground)
    {
        const int defaultFg = 256;
        const int defaultBg = 257;
        const int rgbMode = 1;

        if (mode == rgbMode)
        {
            var r = (color >> 16) & 0xFF;
            var g = (color >> 8) & 0xFF;
            var b = color & 0xFF;
            sb.Append(isForeground ? ";38;2;" : ";48;2;")
              .Append(r).Append(';').Append(g).Append(';').Append(b);
            return;
        }

        // Palette. The default sentinels need nothing: the leading reset already selected them.
        if (isForeground && color == defaultFg) return;
        if (!isForeground && color == defaultBg) return;

        if (color < 8)
            sb.Append(';').Append((isForeground ? 30 : 40) + color);
        else if (color < 16)
            sb.Append(';').Append((isForeground ? 90 : 100) + (color - 8));
        else
            sb.Append(isForeground ? ";38;5;" : ";48;5;").Append(color);
    }

    /// <summary>
    /// Emits the DEC private modes that a repaint does not carry.
    /// </summary>
    /// <remarks>
    /// Only modes whose state is observable and restorable are emitted. Mouse tracking is
    /// deliberately omitted: the receiving side's input plumbing decides whether it wants mouse
    /// reports, and turning them on for a client that does not handle them fills the session with
    /// escape sequences typed as text.
    /// </remarks>
    private static void SerializeModes(Terminal terminal, StringBuilder sb)
    {
        // 25 — cursor visibility. Emitted in both directions: a full-screen program that hid the
        // cursor must not get it back on reattach.
        sb.Append(Csi).Append(terminal.CursorVisible ? "?25h" : "?25l");

        if (terminal.ApplicationCursorKeys) sb.Append(Csi).Append("?1h");
        if (terminal.BracketedPasteMode) sb.Append(Csi).Append("?2004h");
        if (terminal.OriginMode) sb.Append(Csi).Append("?6h");
        if (terminal.ReverseVideo) sb.Append(Csi).Append("?5h");
        if (terminal.InsertMode) sb.Append(Csi).Append("4h");
    }
}
