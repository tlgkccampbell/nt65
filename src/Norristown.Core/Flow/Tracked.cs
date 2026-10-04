namespace Norristown.Flow;

/// <summary>
/// Specifies each register, flag and width that the <see cref="SourceWalk"/> follows.
/// </summary>
internal enum Tracked
{
    /// <summary>The accumulator, or its low byte on the 65816.</summary>
    A,

    /// <summary>The high byte of the 65816's accumulator, which is reported as part of A.</summary>
    AHigh,

    /// <summary>The X index register.</summary>
    X,

    /// <summary>The Y index register.</summary>
    Y,

    /// <summary>The carry flag.</summary>
    C,

    /// <summary>The negative flag.</summary>
    N,

    /// <summary>The zero flag.</summary>
    Z,

    /// <summary>The overflow flag.</summary>
    V,

    /// <summary>The 65816's accumulator width.</summary>
    M,

    /// <summary>The 65816's index width.</summary>
    Index,
}
