namespace Norristown.Flow;

/// <summary>
/// Represents the data of a <see cref="DataMap"/> that lies on no <see cref="DirectPage"/>: the
/// locations declared in one segment, the locations at addresses the source fixes, or the
/// hardware registers among those.
/// </summary>
/// <param name="Name">The segment's name, or null for the locations at addresses the source fixes.</param>
/// <param name="IsHardware">
/// Whether the locations are hardware registers, which <c>.mmio</c> declares. Only a group without
/// a name holds them.
/// </param>
/// <param name="Locations">
/// The locations, in address order where the addresses are known, and otherwise in the order they
/// are declared.
/// </param>
public sealed record DataSegment(string? Name, bool IsHardware, IReadOnlyList<DataLocation> Locations)
{
    /// <summary>
    /// Gets the strongest way that any of the locations is shared, or
    /// <see cref="DataRelation.Unused"/> for a group without locations.
    /// </summary>
    public DataRelation Relation => Locations.Count == 0 ? DataRelation.Unused : Locations.Min(location => location.Relation);

    /// <summary>Gets a value indicating whether a use of one of the locations is a hazard.</summary>
    public bool IsHazard => Locations.Any(location => location.IsHazard);
}
