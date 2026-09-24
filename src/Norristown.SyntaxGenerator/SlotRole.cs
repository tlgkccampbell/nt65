namespace Norristown.SyntaxGenerator;

/// <summary>Specifies what a property row under a node in the table describes.</summary>
public enum SlotRole
{
    /// <summary>A slot of the node, with a position of its own in the node's fixed layout.</summary>
    Slot,

    /// <summary>A property computed from other properties, which has no slot of its own.</summary>
    Member,
}
