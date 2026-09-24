namespace Norristown.Flow;

/// <summary>Represents an edge from one block to a block that may run after it.</summary>
/// <param name="To">The index in the routine of the block reached.</param>
/// <param name="Kind">The reason control reaches it.</param>
public readonly record struct FlowEdge(int To, EdgeKind Kind);
