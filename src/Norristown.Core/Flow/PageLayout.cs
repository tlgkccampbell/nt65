namespace Norristown.Flow;

/// <summary>
/// Says where the address of a <see cref="PageLocation"/> comes from, and so how far a
/// <see cref="DirectPageMap"/> can trust it. Where data lands is decided by ld65, which nt65 does
/// not run, so the address of data in a segment is either read from the last build or predicted.
/// </summary>
public enum PageLayout
{
    /// <summary>The source fixes the address, as an address alias, <c>.mmio</c> or a constant operand does.</summary>
    Fixed,

    /// <summary>The address comes from the debug file of the last build, which says where ld65 put the data.</summary>
    Built,

    /// <summary>The address is predicted from a linked configuration, which places the segment in a memory area.</summary>
    Configured,

    /// <summary>The address is guessed from the page's base, because no linked configuration places the segment.</summary>
    Guessed,
}
