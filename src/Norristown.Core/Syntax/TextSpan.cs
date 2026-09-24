namespace Norristown.Syntax;

/// <summary>Represents a range of characters in a file, as an offset and a length.</summary>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters.</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>Gets the offset just past the end of the range.</summary>
    public int End => Start + Length;

    /// <summary>
    /// Checks whether <paramref name="position"/> is one of the range's characters. An empty
    /// range contains no position, not even its own start.
    /// </summary>
    /// <param name="position">An offset in the file's text.</param>
    public bool Contains(int position) => position >= Start && position < End;

    /// <summary>
    /// Checks whether this range contains the whole of <paramref name="span"/>. A range contains
    /// an empty span at its start and at its end, and it contains itself.
    /// </summary>
    /// <param name="span">The range to look for.</param>
    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;
}
