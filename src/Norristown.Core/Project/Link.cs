namespace Norristown.Project;

/// <summary>
/// Represents one entry of <c>links</c> in <c>nt65.json</c>: an ld65 linker configuration that the
/// program's output is linked with, and what nt65 needs to know about it that the configuration
/// cannot say. A link's configuration declares the segments it places. A program linked more
/// than once, such as a cartridge and a sound file built from some of the same objects, has a
/// link for each.
/// </summary>
/// <param name="Name">The link's name, which a configuration uses to replace it.</param>
/// <param name="ConfigPath">The logical path of the linker configuration.</param>
/// <param name="Declaration">Where the project file names the link.</param>
public sealed record Link(string Name, string ConfigPath, Span Declaration)
{
    /// <summary>Gets the linker configuration, or null when it could not be read.</summary>
    public LinkerConfig? Config { get; init; }

    /// <summary>
    /// Gets the address space everything in this link is in, or null for the host's. A memory
    /// area's own <c>space</c> takes precedence.
    /// </summary>
    public string? Space { get; init; }

    /// <summary>Gets what the project says about the configuration's memory areas, by name.</summary>
    public IReadOnlyList<Area> Memory { get; init; } = [];

    /// <summary>
    /// Represents what the project says about one memory area of a link's configuration: the
    /// facts about the hardware that the linker has no word for.
    /// </summary>
    /// <param name="Name">The memory area's name, as the configuration gives it.</param>
    /// <param name="Declaration">Where the project file names the area.</param>
    public sealed record Area(string Name, Span Declaration)
    {
        /// <summary>
        /// Gets the other banks in which the area's memory is seen, such as low WRAM in banks
        /// <c>$00-$3f</c>. Segments that run in the area are mirrored there.
        /// </summary>
        public IReadOnlyList<(long First, long Last)> Mirrors { get; init; } = [];

        /// <summary>Gets the address space the area is in, or null to leave it to the link.</summary>
        public string? Space { get; init; }
    }
}
