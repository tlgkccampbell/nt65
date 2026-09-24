namespace Norristown.Flow;

/// <summary>Identifies why one block follows another.</summary>
public enum EdgeKind
{
    /// <summary>The block above ran off its end into this one.</summary>
    FallThrough,

    /// <summary>A branch or a jump names this block's label.</summary>
    Taken,

    /// <summary>A call. The block the call returns to is the one after the call, not this one.</summary>
    Call,

    /// <summary>A <c>.next</c> declared this successor because the operand could not say where control goes.</summary>
    Declared,
}
