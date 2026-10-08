namespace Norristown.Flow;

/// <summary>
/// Specifies how the routines of a program share one location on a <see cref="DirectPage"/>, as
/// <see cref="DirectPageMap"/> works it out. The members are in order of strength, so the strongest
/// relation among a page's locations is the one with the lowest value.
/// </summary>
public enum PageRelation
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
