namespace Norristown.Semantics;

/// <summary>
/// Represents the memory area that one linked configuration runs a segment in, with the range of
/// addresses the area covers. Two different areas of one configuration that cover exactly the
/// same addresses are alternatives, so segments that run in them are never mapped at the same
/// moment. The switchable banks of a cartridge mapper and disk overlays that share a load
/// address are laid out this way.
/// </summary>
/// <param name="Config">The logical path of the configuration.</param>
/// <param name="Area">The name of the memory area, as the configuration gives it.</param>
/// <param name="First">The first address of the area.</param>
/// <param name="Last">The last address of the area.</param>
/// <param name="Declaration">Where the configuration declares the area.</param>
public sealed record RunArea(string Config, string Area, long First, long Last, Span Declaration)
{
    /// <summary>
    /// Returns a value indicating whether this area and <paramref name="other"/> are different
    /// areas of the same configuration that cover the same addresses.
    /// </summary>
    /// <remarks>
    /// Areas that only partly overlap are not taken to exclude each other. A configuration often
    /// gives an area more room than the program fills, such as a program area whose size runs on
    /// into the area where its variables live, and ld65 accepts that. Only the same range of
    /// addresses says that the two are alternatives.
    /// </remarks>
    public bool Excludes(RunArea other) =>
        Config == other.Config && Area != other.Area && First == other.First && Last == other.Last;
}
