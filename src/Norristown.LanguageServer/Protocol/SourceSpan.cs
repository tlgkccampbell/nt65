namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one place where an input's value was set.</summary>
/// <param name="Range">The whole line to highlight, on one line.</param>
/// <param name="Kind">What set the value: <c>instruction</c>, <c>macro</c>, <c>call</c>, <c>entry</c> or <c>unknown</c>.</param>
/// <param name="Confidence">How sure the analysis is: <c>proven</c>, or <c>bestEffort</c> for a guess about memory.</param>
/// <param name="Blocker">The line where the analysis lost track, for the kind <c>unknown</c>, or null.</param>
/// <param name="Reason">
/// A short phrase saying why the analysis lost track, or what might have changed a best-effort
/// source, or null.
/// </param>
internal sealed record SourceSpan(Range Range, string Kind, string Confidence, Range? Blocker, string? Reason);
