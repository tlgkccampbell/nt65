namespace Norristown.SyntaxGenerator;

/// <summary>One slot of a node's fixed layout, whichever class declared it.</summary>
/// <param name="Declarer">The row that declares the slot: the node itself, or a class above it.</param>
/// <param name="Slot">The slot.</param>
/// <param name="Index">Where it sits among the node's slots, counting from 0.</param>
public sealed record LaidOutSlot(NodeRow Declarer, NodeSlot Slot, int Index);
