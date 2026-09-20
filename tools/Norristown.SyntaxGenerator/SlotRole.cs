namespace Norristown.SyntaxGenerator;

/// <summary>What a row under a node describes.</summary>
public enum SlotRole
{
    /// <summary>A piece of the node, with a place of its own in the node's fixed layout.</summary>
    Slot,

    /// <summary>A property worked out from other ones, which holds no piece of its own.</summary>
    Member,

    /// <summary>A property that stands in for slots until the kind is converted, and goes then.</summary>
    Legacy,
}
