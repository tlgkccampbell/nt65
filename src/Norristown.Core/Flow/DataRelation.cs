namespace Norristown.Flow;

/// <summary>
/// Specifies how the routines of a program share one location, as <see cref="DataMap"/> works it
/// out. The members are in order of strength, so the strongest relation among the locations of a
/// page or a segment is the one with the lowest value.
/// </summary>
public enum DataRelation
{
    /// <summary>
    /// A routine relies on the location across a call to a routine that uses it as a temporary
    /// of its own.
    /// </summary>
    Nested,

    /// <summary>
    /// An interrupt and the code it interrupts both use the location. One routine that runs both
    /// in an interrupt and outside one is enough.
    /// </summary>
    Interrupt,

    /// <summary>More than one routine uses the location.</summary>
    Shared,

    /// <summary>One routine uses the location.</summary>
    Own,

    /// <summary>The location is a hardware register, which <c>.mmio</c> declares.</summary>
    Hardware,

    /// <summary>No instruction reaches the location.</summary>
    Unused,
}
