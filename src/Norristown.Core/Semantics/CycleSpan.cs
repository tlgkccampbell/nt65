namespace Norristown.Semantics;

/// <summary>
/// Represents the value of <c>.mincycles</c> or <c>.maxcycles</c> over a span of code, or the
/// reason it has no value. When nt65 cannot bound a span, it records which of two causes it
/// found, a call or a loop, so that the expression is never left with no value and no reason.
/// </summary>
/// <param name="Value">The cycle count, or null when the span has none.</param>
/// <param name="Problem">The feature of the span that prevents a bound, or null.</param>
public readonly record struct CycleSpan(long? Value, string? Problem);
