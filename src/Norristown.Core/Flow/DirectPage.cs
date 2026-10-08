namespace Norristown.Flow;

/// <summary>
/// Represents one direct page of a <see cref="DirectPageMap"/>: the 256 bytes that start at one
/// value of D, with the locations the program reaches there. On the processors without a D register
/// the only page is the zero page. One page of a map stands for every access made while D was not
/// known, and has no base.
/// </summary>
/// <param name="Base">The value of D, or null for the page that stands for D not being known.</param>
/// <param name="Segments">The segments whose symbols are reached through this page, by name.</param>
/// <param name="IsHardware">Whether every location on the page is a hardware register.</param>
/// <param name="Locations">The locations on the page, by offset, with those whose offset is not known last.</param>
/// <param name="Unknown">The routines that reach memory through D while it is not known, for the page without a base.</param>
/// <param name="Direct">The number of instructions that reach a location through this page with direct addressing.</param>
/// <param name="Notes">
/// The facts about the page's layout that are about no one line, such as a segment that lies
/// outside the page. None of them is a problem with the program; each says how far to trust the
/// layout shown.
/// </param>
public sealed record DirectPage(
    long? Base, IReadOnlyList<string> Segments, bool IsHardware, IReadOnlyList<PageLocation> Locations,
    IReadOnlyList<UnknownPageUse> Unknown, int Direct, IReadOnlyList<PageNote> Notes)
{
    /// <summary>Gets the pages that cover some of the same addresses as this one.</summary>
    public IReadOnlyList<PageOverlap> Overlaps { get; internal set; } = [];

    /// <summary>
    /// Gets the strongest way that any location on the page is shared, or
    /// <see cref="PageRelation.Unused"/> for a page without locations.
    /// </summary>
    public PageRelation Relation => Locations.Count == 0 ? PageRelation.Unused : Locations.Min(location => location.Relation);

    /// <summary>Gets a value indicating whether a use of some location on the page is a hazard.</summary>
    public bool IsHazard => Locations.Any(location => location.IsHazard) || Unknown.Any(use => use.Use.Hazards.Count > 0);

    /// <summary>
    /// Gets the number of bytes the page's locations take, counting only those whose offsets are
    /// known. A byte that two locations on the page take counts once.
    /// </summary>
    public long Used => Locations
        .Where(location => location.Offset is not null && location.Size is > 0)
        .SelectMany(location => Enumerable.Range(0, (int)location.Size!.Value).Select(i => location.Offset!.Value + i))
        .Distinct()
        .LongCount();
}
