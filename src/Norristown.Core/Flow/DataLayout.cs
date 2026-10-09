namespace Norristown.Flow;

/// <summary>
/// Says where the address of a <see cref="DataLocation"/> comes from, and so how far a
/// <see cref="DataMap"/> can trust it. Where data lands is decided by ld65, which nt65 does
/// not run, so the address of data in a segment is read from the last build. Without a build, it is
/// predicted on a page and not known off the pages.
/// </summary>
public enum DataLayout
{
    /// <summary>The source fixes the address, as an address alias, <c>.mmio</c> or a constant operand does.</summary>
    Fixed,

    /// <summary>The address comes from the debug file of the last build, which says where ld65 put the data.</summary>
    Built,

    /// <summary>The address is predicted from a linked configuration, which places the segment in a memory area.</summary>
    Configured,

    /// <summary>The address is guessed from the page's base, because no linked configuration places the segment.</summary>
    Guessed,

    /// <summary>
    /// The address is not known, because the data lies on no page and there is no build. The map
    /// predicts addresses only on a page, whose layout it can work out from the segments alone.
    /// </summary>
    Unknown,
}
