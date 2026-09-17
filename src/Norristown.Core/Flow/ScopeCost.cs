using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What one pass through an inline <c>.scope</c> block of a routine costs: from falling into
/// it at the top to leaving it, wherever it is left.
/// </summary>
/// <param name="Opener">The line that opens the block, where the cost belongs.</param>
/// <param name="Cost">What the pass costs.</param>
public readonly record struct ScopeCost(TextSpan Opener, RoutineCost Cost);
