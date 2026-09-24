using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the cost of one pass through an inline <c>.scope</c> block of a routine, from
/// falling into it at the top to leaving it at any exit.
/// </summary>
/// <param name="Opener">The line that opens the block, to which the cost belongs.</param>
/// <param name="Cost">What the pass costs.</param>
public readonly record struct ScopeCost(TextSpan Opener, RoutineCost Cost);
