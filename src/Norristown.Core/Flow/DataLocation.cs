using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents one location of a <see cref="DataMap"/>, with the routines that use it. A location is a
/// data declaration, or an address that the source fixes and that the program reaches.
/// </summary>
/// <param name="Symbol">
/// The symbol that names the location, or null for a constant address that an instruction reaches
/// through the page without a symbol, such as <c>lda $FB</c>.
/// </param>
/// <param name="Name">
/// The name the location is shown by. It is the symbol's name, or for a location without a symbol
/// its address in four hexadecimal digits, such as <c>$00FB</c>. No two locations on a page share one.
/// </param>
/// <param name="Offset">
/// The offset from D to the location's first byte, or null when it is not known or the location
/// lies on no <see cref="DirectPage"/>.
/// </param>
/// <param name="Size">The number of bytes the location takes, or null when it is not known.</param>
/// <param name="Layout">
/// Where the location's address comes from. Only the source or the last build fixes it, because
/// only the linker decides where data lands, and any other address is a prediction.
/// </param>
/// <param name="Type">The element the location is declared with, such as <c>.word</c>, or an empty string.</param>
/// <param name="Uses">
/// What each routine that reaches the location does with it, by routine. A routine that names the
/// location while D is not known is among them, marked by <see cref="DataUse.IsUnknownPage"/>.
/// </param>
public sealed record DataLocation(
    Symbol? Symbol, string Name, long? Offset, long? Size, DataLayout Layout, string Type, IReadOnlyList<DataUse> Uses)
{
    /// <summary>Gets how the routines that use the location share it.</summary>
    public DataRelation Relation { get; internal set; }

    /// <summary>Gets a value indicating whether a routine's use of the location is a hazard.</summary>
    public bool IsHazard => Uses.Any(use => use.Hazards.Count > 0);

    /// <summary>
    /// Gets the runs of addresses the location shares with other locations, on other pages or on
    /// its own. <see cref="SharedBytes.Kind"/> tells them apart.
    /// </summary>
    public IReadOnlyList<SharedBytes> Shared { get; internal set; } = [];

    /// <summary>
    /// Gets the address of the location's first byte, or null when it is not known. On a page it
    /// may be predicted, as <see cref="Layout"/> says. Off the pages it comes only from the source
    /// or the last build.
    /// </summary>
    public long? Address { get; init; }

    /// <summary>
    /// Gets the statements that take the location's address without reaching it, such as
    /// <c>ldx #tmp</c>, <c>lda #&lt;ptr</c> or <c>.addr tmp</c> in a table. The program uses such a
    /// location through a pointer or an index that the map cannot follow, so it is not unused even
    /// when its <see cref="Relation"/> is <see cref="DataRelation.Unused"/>. Inside a macro
    /// expansion, the statement is the outermost call in the statement's file.
    /// </summary>
    public IReadOnlyList<DataReference> References { get; init; } = [];

    /// <summary>Gets a value indicating whether the location is a hardware register, which <c>.mmio</c> declares.</summary>
    public bool IsHardware => Relation == DataRelation.Hardware;

    /// <summary>
    /// Gets a value indicating whether an instruction reaches the location. A hardware register
    /// that lies on a page is shown there even when no instruction reaches it, so that the page
    /// names every register it covers.
    /// </summary>
    public bool IsReached => Uses.Count > 0;
}
