namespace Norristown.Flow;

/// <summary>Why one block follows another.</summary>
public enum EdgeKind
{
    /// <summary>The block above ran off its end into this one.</summary>
    FallThrough,

    /// <summary>A branch or a jump names this block's label.</summary>
    Taken,

    /// <summary>A call: the block it returns to is the one after the call, not this one.</summary>
    Call,

    /// <summary>A <c>.next</c> said so, where the operand could not.</summary>
    Declared,
}
