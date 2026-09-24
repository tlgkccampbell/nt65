using Norristown.Processor;

namespace Norristown.Project;

/// <summary>
/// Represents one entry of <c>segments</c> in <c>nt65.json</c>, as the file gives it. Without
/// <c>links</c>, the entry declares the segment and needs a <c>size</c>. With them, the linked
/// configuration declares the segment, and the entry adds only what the configuration cannot say.
/// </summary>
/// <param name="Name">The segment's name.</param>
/// <param name="Declaration">Where the project file names the segment.</param>
public sealed record ProjectSegment(string Name, Span Declaration)
{
    /// <summary>Gets the entry's <c>size</c>, or null when it gives none.</summary>
    public AddressSize? Size { get; init; }

    /// <summary>Gets the entry's <c>dp</c>, or null when it gives none.</summary>
    public Given? DirectPage { get; init; }

    /// <summary>Gets the entry's <c>bank</c>, or null when it gives none.</summary>
    public Given? Bank { get; init; }

    /// <summary>Gets the entry's <c>mirrors</c>, or null when it gives none.</summary>
    public IReadOnlyList<(long First, long Last)>? Mirrors { get; init; }

    /// <summary>Gets the entry's <c>space</c>, or null when it gives none.</summary>
    public string? Space { get; init; }
}
