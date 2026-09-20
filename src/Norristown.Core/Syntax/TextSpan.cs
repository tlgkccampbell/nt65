namespace Norristown.Syntax;

/// <summary>A range of characters in a file, as an offset and a length.</summary>
/// <param name="Start">Offset of the first character.</param>
/// <param name="Length">How many characters.</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>The offset just past the range.</summary>
    public int End => Start + Length;

    /// <summary>
    /// Whether <paramref name="position"/> is one of the range's characters. A range of no
    /// length holds no position, not even its own start.
    /// </summary>
    /// <param name="position">An offset in the file's text.</param>
    public bool Contains(int position) => position >= Start && position < End;

    /// <summary>
    /// Whether this range holds the whole of <paramref name="span"/>. A range holds an empty
    /// span at its start and at its end, and holds itself.
    /// </summary>
    /// <param name="span">The range to look for.</param>
    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;
}
