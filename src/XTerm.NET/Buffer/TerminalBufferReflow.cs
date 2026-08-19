namespace XTerm.Buffer;

/// <summary>
/// Width-change reflow for <see cref="TerminalBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Without reflow, changing the width resizes each physical line independently: narrowing clips
/// the right-hand side of every long line and widening leaves previously-wrapped lines broken at
/// the old margin. Both are lossy in a way users notice immediately, because resizing a window is
/// an ordinary thing to do and the text that disappears does not come back.
/// </para>
/// <para>
/// Reflow instead treats a line and its <see cref="BufferLine.IsWrapped"/> continuations as one
/// LOGICAL line — which is what the program wrote — and re-splits it at the new width. This is
/// what tmux does on a client resize and what xterm.js does in its reflow path.
/// </para>
/// <para>
/// Applies to the normal buffer only. The alternate buffer is not reflowed on purpose: a
/// full-screen program owns that surface entirely and repaints it in response to SIGWINCH, so
/// rearranging its cells underneath produces a frame the program never drew and will overwrite
/// anyway.
/// </para>
/// </remarks>
internal static class TerminalBufferReflow
{
    /// <summary>One logical line: its cells, and where the cursor sits within it.</summary>
    private sealed class LogicalLine
    {
        public readonly List<BufferCell> Cells = new List<BufferCell>();
        /// <summary>Offset of the cursor within <see cref="Cells"/>, or -1 when not on this line.</summary>
        public int CursorOffset = -1;
    }

    /// <summary>
    /// Re-splits <paramref name="lines"/> at <paramref name="newCols"/>, preserving logical lines.
    /// </summary>
    /// <param name="lines">Physical lines, oldest first.</param>
    /// <param name="oldCols">Width the lines are currently laid out at.</param>
    /// <param name="newCols">Width to lay them out at.</param>
    /// <param name="cursorRow">Absolute row of the cursor within <paramref name="lines"/>.</param>
    /// <param name="cursorCol">Column of the cursor on that row.</param>
    /// <param name="newCursorRow">Absolute row of the cursor after reflow.</param>
    /// <param name="newCursorCol">Column of the cursor after reflow.</param>
    /// <returns>The reflowed physical lines, oldest first.</returns>
    public static List<BufferLine> Reflow(
        IReadOnlyList<BufferLine?> lines,
        int oldCols,
        int newCols,
        int cursorRow,
        int cursorCol,
        out int newCursorRow,
        out int newCursorCol)
    {
        var logical = BuildLogicalLines(lines, oldCols, cursorRow, cursorCol);
        return SplitLogicalLines(logical, newCols, out newCursorRow, out newCursorCol);
    }

    private static List<LogicalLine> BuildLogicalLines(
        IReadOnlyList<BufferLine?> lines, int oldCols, int cursorRow, int cursorCol)
    {
        var logical = new List<LogicalLine>();
        LogicalLine? current = null;

        for (var row = 0; row < lines.Count; row++)
        {
            var line = lines[row];
            if (line is null)
                continue;

            // A line that is not a continuation starts a new logical line.
            if (current is null || !line.IsWrapped)
            {
                current = new LogicalLine();
                logical.Add(current);
            }

            var offsetInLogical = current.Cells.Count;

            // Take the full width for a line that continues, because the blanks in the middle of
            // a wrapped run are real content. Trim only where the run ends.
            var take = row + 1 < lines.Count && lines[row + 1]?.IsWrapped == true
                ? oldCols
                : line.GetTrimmedLength();

            for (var col = 0; col < take; col++)
                current.Cells.Add(line[col]);

            if (row == cursorRow)
            {
                // The cursor can sit one past the last cell (pending wrap), so it is placed by
                // offset rather than by finding a cell.
                current.CursorOffset = offsetInLogical + cursorCol;
            }
        }

        return logical;
    }

    private static List<BufferLine> SplitLogicalLines(
        List<LogicalLine> logical, int newCols, out int newCursorRow, out int newCursorCol)
    {
        var result = new List<BufferLine>();
        newCursorRow = 0;
        newCursorCol = 0;

        var fill = BufferCell.Space;

        foreach (var logicalLine in logical)
        {
            var startRow = result.Count;
            var cells = logicalLine.Cells;

            // An empty logical line is still a line: the program wrote a bare newline and the
            // blank row is part of the layout.
            if (cells.Count == 0)
            {
                result.Add(new BufferLine(newCols, fill));
                if (logicalLine.CursorOffset >= 0)
                {
                    newCursorRow = startRow;
                    newCursorCol = Math.Min(logicalLine.CursorOffset, newCols - 1);
                }
                continue;
            }

            for (var offset = 0; offset < cells.Count; offset += newCols)
            {
                var line = new BufferLine(newCols, fill);

                // Every chunk after the first is a continuation, which is what lets a later
                // reflow rejoin them.
                line.IsWrapped = offset > 0;

                var count = Math.Min(newCols, cells.Count - offset);
                for (var i = 0; i < count; i++)
                {
                    var cell = cells[offset + i];
                    line.SetCell(i, ref cell);
                }

                result.Add(line);
            }

            if (logicalLine.CursorOffset >= 0)
            {
                var offset = logicalLine.CursorOffset;
                newCursorRow = startRow + offset / newCols;
                newCursorCol = offset % newCols;

                // A cursor exactly at a wrap boundary lands at the start of a row that may not
                // exist yet (it was one past the end). Anchor it to the last produced row.
                if (newCursorRow >= result.Count)
                {
                    newCursorRow = result.Count - 1;
                    newCursorCol = Math.Min(offset - (newCursorRow - startRow) * newCols, newCols - 1);
                }
            }
        }

        if (result.Count == 0)
            result.Add(new BufferLine(newCols, fill));

        return result;
    }
}
