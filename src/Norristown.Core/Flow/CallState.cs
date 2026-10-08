using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents the processor state at one place that hands control to a routine's entry: a call,
/// a tail call, a <c>.fallthrough</c> or a <c>.next</c> that names the routine.
/// <see cref="InferredSignatures"/> works out a routine's entry from what these agree on.
/// </summary>
/// <param name="Caller">The routine the call is made from.</param>
/// <param name="Target">The routine called.</param>
/// <param name="State">The state where the call is made.</param>
/// <param name="At">Where the call is made.</param>
internal sealed record CallState(Symbol Caller, Symbol Target, ProcessorState State, Span At);
