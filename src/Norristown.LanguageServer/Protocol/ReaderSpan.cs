namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one place that reads an output's value.</summary>
/// <param name="Range">The whole line to highlight, on one line.</param>
/// <param name="Kind">
/// How the line reads the value: <c>instruction</c>, <c>macro</c> or <c>call</c>, or <c>exit</c>
/// where the value leaves the routine and what it returns to may read it.
/// </param>
/// <param name="Confidence">How sure the analysis is: <c>proven</c>, or <c>bestEffort</c> for a guess about memory.</param>
/// <param name="Reason">
/// A short phrase saying what might have changed a value in memory between the caret and this
/// reader, which starts <c>or possibly</c>, or null.
/// </param>
internal sealed record ReaderSpan(Range Range, string Kind, string Confidence, string? Reason);
