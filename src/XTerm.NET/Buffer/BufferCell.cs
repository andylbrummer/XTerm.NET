using System.Diagnostics;
using System.Text;
using XTerm.Common;

namespace XTerm.Buffer;

/// <summary>
/// Represents a single cell in the terminal buffer.
/// Each cell contains a character, width, and attributes.
/// </summary>
[DebuggerDisplay("'{Content}'  [{Width}, {Attributes}, {CodePoint}]")]
public struct BufferCell : IEquatable<BufferCell>
{
    public string Content = String.Empty;
    public int Width = 0;
    public AttributeData Attributes = AttributeData.Default;
    public int CodePoint = 0;

    /// <summary>
    /// OSC 8 hyperlink target for this cell, or null when the cell is not part of a link.
    ///
    /// <para>
    /// Cell state rather than terminal state, because that is what a link IS: OSC 8 opens a run,
    /// the cells written inside it carry the target, and the closing sequence ends the run. The
    /// terminal's <c>CurrentHyperlink</c> only says what the NEXT cell would get. Without it
    /// stored here a link exists solely as a transient event: it can be rendered as it arrives,
    /// but it cannot be found again by hit-testing the buffer, cannot survive a redraw, and
    /// cannot be serialized.
    /// </para>
    /// <para>
    /// Every cell in a run shares one string reference (the handler holds it for the run's
    /// duration), so the cost is the reference, not a copy per cell.
    /// </para>
    /// </summary>
    public string? Hyperlink = null;

    public static BufferCell Empty => new BufferCell();

    public static BufferCell Space => new BufferCell
    {
        Content = " ",
        Width = 1,
        Attributes = AttributeData.Default,
        CodePoint = 0x20
    };

    public BufferCell()
    {
        Content = String.Empty;
        Attributes = AttributeData.Default;
    }
    public BufferCell(string content, int width, AttributeData attributes)
    {
        Content = content;
        Width = width;
        Attributes = attributes;
        CodePoint = content.Length > 0 ? char.ConvertToUtf32(content, 0) : 0;
    }

    public BufferCell(int codePoint, int width, AttributeData attributes)
    {
        CodePoint = codePoint;
        Width = width;
        Attributes = attributes;
        Content = char.ConvertFromUtf32(codePoint);
    }

    public bool IsEmpty() => CodePoint == Empty.CodePoint;

    public bool IsSpace() => CodePoint == Space.CodePoint;

    /// <summary>
    /// Includes <see cref="Hyperlink"/>: two cells with identical glyphs and attributes but
    /// different link targets are not the same cell, and a consumer that coalesces runs by
    /// equality would otherwise merge across a link boundary.
    /// </summary>
    public bool Equals(BufferCell other)
    {
        return Content == other.Content &&
               Width == other.Width &&
               Attributes.Equals(other.Attributes) &&
               CodePoint == other.CodePoint &&
               Hyperlink == other.Hyperlink;
    }

    public override bool Equals(object? obj)
    {
        return obj is BufferCell other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Width, Attributes, CodePoint, Hyperlink);
    }

    public static bool operator ==(BufferCell left, BufferCell right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(BufferCell left, BufferCell right)
    {
        return !left.Equals(right);
    }
}
