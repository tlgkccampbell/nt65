namespace Norristown.SyntaxGenerator;

/// <summary>What a property row under a node in the table describes.</summary>
public enum SlotRole
{
    /// <summary>A piece of the node, with a place of its own in the node's fixed layout.</summary>
    Slot,

    /// <summary>A property computed from other ones, which holds no piece of its own.</summary>
    Member,
}
