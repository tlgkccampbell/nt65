using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// Represents what <c>nt65.json</c> says about a program. A single file built without a project
/// file uses <see cref="None"/>, so nothing downstream has to ask whether there is a project.
/// </summary>
/// <param name="Cpu">The processor the program is built for. A <c>.cpu</c> item must agree with it.</param>
/// <param name="Files">Globs naming the source files. Their order is not significant.</param>
/// <param name="Out">
/// The output directory, under which each module's <c>.s</c> is written at a path made from its
/// module name, regardless of where its source is. When null, output is written under the
/// project root.
/// </param>
/// <param name="Defines">The build configuration's defines, together with any that <c>-D</c> added or overrode.</param>
/// <param name="Segments">Segments declared by the project rather than by a file.</param>
/// <param name="Diagnostics">
/// The problems found while reading the project and the command line that added to it. A
/// project that cannot be read is reported as a problem with the program rather than making the
/// reader fail, so these diagnostics travel with the settings and are reported alongside all
/// the others.
/// </param>
public sealed record ProjectSettings(
    Cpu? Cpu,
    IReadOnlyList<string> Files,
    string? Out,
    IReadOnlyList<Define> Defines,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The severities of a project that overrides no diagnostic, which is an empty map.</summary>
    public static readonly IReadOnlyDictionary<string, Severity?> NoSeverities =
        new SortedDictionary<string, Severity?>(StringComparer.Ordinal);

    /// <summary>Gets the address spaces other than the host's, ordered by name.</summary>
    public IReadOnlyList<AddressSpace> Spaces { get; init; } = [];

    /// <summary>
    /// Gets the ranges, ordered by address, each with the banks from which an absolute constant
    /// address in it may be reached.
    /// </summary>
    public IReadOnlyList<AccessRange> Ranges { get; init; } = [];

    /// <summary>
    /// Gets the severity at which each named diagnostic is reported, overriding the one the
    /// catalogue gives it. A name mapped to null is not reported at all. An error is never
    /// lowered, and a project file that asks for that gets an error at the entry that asks.
    /// </summary>
    public IReadOnlyDictionary<string, Severity?> Severities { get; init; } = NoSeverities;

    /// <summary>Gets the settings for no project, which <c>nt65 build main.nt65</c> works from.</summary>
    public static ProjectSettings None { get; } = new(null, [], null, [], [], []);

    /// <summary>Gets the named configurations in name order. A build chooses one of them or none.</summary>
    public IReadOnlyList<BuildConfiguration> Configurations { get; init; } = [];

    /// <summary>
    /// Returns the settings that the configuration named <paramref name="name"/> builds with,
    /// which are its defines over the project's, and its <c>out</c> if it gives one. If the
    /// project has no configuration with that name, reports an error at <paramref name="given"/>
    /// and uses the project's own settings.
    /// </summary>
    public ProjectSettings Configured(string name, Span given)
    {
        if (Configurations.FirstOrDefault(configuration => configuration.Name == name) is { } chosen)
        {
            return With(chosen.Defines) with
            {
                Out = chosen.Out ?? Out,
                Severities = Reported(chosen.Severities),
            };
        }
        var named = Configurations.Select(configuration => $"`{configuration.Name}`").ToList();
        var message = Catalogue.ConfigurationUnknown.Says(
            name,
            named.Count == 0
                ? $"{ProjectFile.Name} names none"
                : $"{ProjectFile.Name} names "
                    + (named.Count == 1 ? named[0] : string.Join(", ", named.SkipLast(1)) + " and " + named[^1]));
        return this with { Diagnostics = [.. Diagnostics, new Diagnostic(given, message)] };
    }

    /// <summary>
    /// Returns the project's severity for each diagnostic, with the entries in
    /// <paramref name="over"/> taking precedence.
    /// </summary>
    private IReadOnlyDictionary<string, Severity?> Reported(IReadOnlyDictionary<string, Severity?> over)
    {
        if (over.Count == 0)
            return Severities;
        var said = new SortedDictionary<string, Severity?>(StringComparer.Ordinal);
        foreach (var (id, level) in Severities.Concat(over))
            said[id] = level;
        return said;
    }

    /// <summary>
    /// Returns these settings with <paramref name="defines"/> added, each replacing any existing
    /// define with the same name.
    /// </summary>
    public ProjectSettings With(IReadOnlyList<Define> defines)
    {
        if (defines.Count == 0)
            return this;
        var byName = Defines.ToDictionary(define => define.Name, StringComparer.Ordinal);
        foreach (var define in defines)
            byName[define.Name] = define;
        return this with { Defines = [.. byName.Values.OrderBy(define => define.Name, StringComparer.Ordinal)] };
    }
}
