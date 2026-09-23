namespace Norristown.Semantics;

/// <summary>
/// What <c>.mincycles</c> or <c>.maxcycles</c> comes to over a span of code, or why it has no
/// value. When nt65 cannot bound a span, it records which of two causes it found — a call or a
/// loop — rather than leaving the expression with no value and no reason.
/// </summary>
/// <param name="Value">The count, or null where the span has none.</param>
/// <param name="Problem">What in the span prevents a bound, or null.</param>
public readonly record struct CycleSpan(long? Value, string? Problem);
