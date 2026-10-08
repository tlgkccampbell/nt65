using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one transfer of control inside a routine that an editor draws as an arrow, from the
/// line that transfers to the line it transfers to. <see cref="FlowArrows"/> says which transfers
/// these are.
/// </summary>
/// <param name="From">The span of the line holding the branch, the jump or the statement the <c>.next</c> is under.</param>
/// <param name="To">The span of the line of the routine that control goes to.</param>
/// <param name="IsDeclared">Whether a <c>.next</c> declared the transfer rather than the statement's operand.</param>
/// <param name="IsProved">Whether the transfer is a conditional branch the flags prove always or never taken.</param>
/// <param name="IsReached">Whether any path from the routine's entry reaches the line that transfers.</param>
public sealed record FlowArrow(TextSpan From, TextSpan To, bool IsDeclared, bool IsProved, bool IsReached);
