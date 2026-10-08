using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents one location on a <see cref="DirectPage"/>, which is a data declaration or an address
/// that the page reaches, with the routines that use it.
/// </summary>
/// <param name="Symbol">
/// The symbol that names the location, or null for a constant address that an instruction reaches
/// through the page without a symbol, such as <c>lda $FB</c>.
/// </param>
/// <param name="Name">
/// The name the location is shown by. It is the symbol's name, or for a location without a symbol
/// its address in four hexadecimal digits, such as <c>$00FB</c>. No two locations on a page share one.
/// </param>
/// <param name="Offset">The offset from D to the location's first byte, or null when it is not known.</param>
/// <param name="Size">The number of bytes the location takes, or null when it is not known.</param>
/// <param name="Layout">
/// Where the location's address comes from. Only the source or the last build fixes it, because
/// only the linker decides where data lands, and any other address is a prediction.
/// </param>
/// <param name="Type">The element the location is declared with, such as <c>.word</c>, or an empty string.</param>
/// <param name="Uses">
/// What each routine that reaches the location does with it, by routine. A routine that names the
/// location while D is not known is among them, marked by <see cref="PageUse.IsUnknownPage"/>.
/// </param>
public sealed record PageLocation(
    Symbol? Symbol, string Name, long? Offset, long? Size, PageLayout Layout, string Type, IReadOnlyList<PageUse> Uses)
{
    /// <summary>Gets how the routines that use the location share it.</summary>
    public PageRelation Relation { get; internal set; }

    /// <summary>Gets a value indicating whether a routine's use of the location is a hazard.</summary>
    public bool IsHazard => Uses.Any(use => use.Hazards.Count > 0);

    /// <summary>Gets the runs of addresses the location shares with locations on other pages.</summary>
    public IReadOnlyList<SharedBytes> Shared { get; internal set; } = [];

    /// <summary>Gets a value indicating whether the location is a hardware register, which <c>.mmio</c> declares.</summary>
    public bool IsHardware => Relation == PageRelation.Hardware;
}
