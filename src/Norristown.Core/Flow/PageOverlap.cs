namespace Norristown.Flow;

/// <summary>Represents the addresses that two pages of a <see cref="DataMap"/> both cover.</summary>
/// <param name="Page">The other page.</param>
/// <param name="First">The first address both cover.</param>
/// <param name="Last">The last address both cover.</param>
/// <param name="Shared">The runs of addresses that a location of each page both take. None means the pages only overlap.</param>
public sealed record PageOverlap(DirectPage Page, long First, long Last, IReadOnlyList<SharedBytes> Shared);
