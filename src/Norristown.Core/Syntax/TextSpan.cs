namespace Norristown.Syntax;

/// <summary>A range of characters in a file, as an offset and a length.</summary>
/// <param name="Start">Offset of the first character.</param>
/// <param name="Length">How many characters.</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>The offset just past the range.</summary>
    public int End => Start + Length;
}
