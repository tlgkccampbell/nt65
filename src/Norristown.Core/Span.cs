namespace Norristown;

/// <summary>A range on one line. Lines and columns are 1-based; <see cref="EndColumn"/> is exclusive.</summary>
/// <param name="File">The file's logical path.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="StartColumn">The 1-based column where the range starts.</param>
/// <param name="EndColumn">The 1-based column just past the range.</param>
public readonly record struct Span(string File, int Line, int StartColumn, int EndColumn);
