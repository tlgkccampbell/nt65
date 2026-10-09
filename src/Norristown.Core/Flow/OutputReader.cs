using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one place that reads the value of an output that <see cref="OutputReaders"/> reports,
/// on a path from the caret.
/// </summary>
/// <param name="Line">The span of the statement that reads the value, in the caret's file.</param>
/// <param name="Kind">How the statement reads it.</param>
/// <param name="Confidence">How sure the analysis is of the reader, which is a best guess for memory.</param>
public sealed record OutputReader(TextSpan Line, ReaderKind Kind, SourceConfidence Confidence);
