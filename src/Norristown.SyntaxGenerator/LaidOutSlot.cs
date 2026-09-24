namespace Norristown.SyntaxGenerator;

/// <summary>Represents one slot of a node's fixed layout, regardless of which class declared it.</summary>
/// <param name="Declarer">The row that declares the slot, which is the node itself or a class above it.</param>
/// <param name="Slot">The slot.</param>
/// <param name="Index">The slot's position among the node's slots, counting from 0.</param>
public sealed record LaidOutSlot(NodeRow Declarer, NodeSlot Slot, int Index);
