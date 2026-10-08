using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the processor state on one line of a routine, which an editor draws as a stripe
/// beside the line. <see cref="WidthStripes"/> says which lines have one.
/// </summary>
/// <param name="Line">The span of the line.</param>
/// <param name="State">
/// The state that reaches the line, as every path and every copy of the line agrees on it. A part
/// on which they disagree is unknown.
/// </param>
public sealed record WidthStripe(TextSpan Line, ProcessorState State);
