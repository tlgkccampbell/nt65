namespace Norristown.Syntax;

/// <summary>A run of whole lines, 0-based and with both ends included.</summary>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
public readonly record struct LineRange(int StartLine, int EndLine);
