using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one place where the value of an input that <see cref="InputSources"/> reports was set,
/// on a path that reaches the caret.
/// </summary>
/// <param name="Kind">What set the value.</param>
/// <param name="Line">
/// The span of the statement that set the value, in the caret's file. For a
/// <see cref="SourceKind.Entry"/> source it is the span of the routine's name, and for a
/// <see cref="SourceKind.Unknown"/> source it is the blocker's span.
/// </param>
/// <param name="Confidence">How sure the analysis is of the source.</param>
/// <param name="Blocker">The span of the statement where the analysis lost track, for an unknown source.</param>
/// <param name="Reason">
/// A short phrase saying why the analysis lost track, for an unknown source, or what might have
/// changed the value, for a best-effort source. It is null otherwise.
/// </param>
public sealed record InputSource(
    SourceKind Kind, TextSpan Line, SourceConfidence Confidence, TextSpan? Blocker, string? Reason);
