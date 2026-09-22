namespace Norristown.Semantics;

/// <summary>
/// What <c>.mincycles</c> or <c>.maxcycles</c> comes to over a span of code, or why it comes to
/// nothing. A span nt65 has no bound for says which of the two it found — a call or a loop —
/// rather than leaving the expression without a value and no reason.
/// </summary>
/// <param name="Value">The count, or null where the span has none.</param>
/// <param name="Problem">What is in the span that leaves it no bound, or null.</param>
public readonly record struct CycleSpan(long? Value, string? Problem);
