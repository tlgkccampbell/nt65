namespace Norristown.Syntax;

/// <summary>Represents a run of whole lines, 0-based, with both ends included.</summary>
/// <param name="StartLine">The index of the first line.</param>
/// <param name="EndLine">The index of the last line.</param>
public readonly record struct LineRange(int StartLine, int EndLine);
