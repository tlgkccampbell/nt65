namespace Norristown.Flow;

/// <summary>One step from a block to a block that may run after it.</summary>
/// <param name="To">The block reached, by its index in the routine.</param>
/// <param name="Kind">Why it is reached.</param>
public readonly record struct FlowEdge(int To, EdgeKind Kind);
