using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one place that reads the value of an output that <see cref="OutputReaders"/> reports,
/// on a path from the caret.
/// </summary>
/// <param name="Line">The span of the statement that reads the value, in the caret's file.</param>
/// <param name="Kind">How the statement reads it.</param>
/// <param name="Confidence">How sure the analysis is of the reader, which is a best guess for memory.</param>
/// <param name="Reason">
/// A short phrase saying what might have changed a value in memory between the caret and the
/// reader, such as <c>or possibly `sta (ptr),y` on line 12</c>, or null where nothing might.
/// </param>
public sealed record OutputReader(TextSpan Line, ReaderKind Kind, SourceConfidence Confidence, string? Reason = null);
