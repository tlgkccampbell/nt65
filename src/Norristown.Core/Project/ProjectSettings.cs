using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// What <c>nt65.json</c> says about a program. A single file built without one uses
/// <see cref="None"/>, so nothing downstream has to ask whether there is a project.
/// </summary>
/// <param name="Cpu">The processor the program is built for; a <c>.cpu</c> item must agree.</param>
/// <param name="Files">Globs naming the source files. Their order is not significant.</param>
/// <param name="Out">Where each <c>foo.s</c> goes, mirroring the source tree; null writes beside the source.</param>
/// <param name="Defines">The build configuration, and whatever <c>-D</c> added or overrode.</param>
/// <param name="Segments">Segments declared by the project rather than by a file.</param>
/// <param name="Diagnostics">
/// What was wrong with reading the project, and with the command line that added to it. A
/// project that cannot be read is the program's problem, not the reader's, so these travel
/// with the settings and are reported alongside everything else.
/// </param>
public sealed record ProjectSettings(
    Cpu? Cpu,
    IReadOnlyList<string> Files,
    string? Out,
    IReadOnlyList<Define> Defines,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>What a project that says nothing about any diagnostic gives.</summary>
    public static readonly IReadOnlyDictionary<string, Severity?> NoSeverities =
        new SortedDictionary<string, Severity?>(StringComparer.Ordinal);

    /// <summary>The address spaces other than the host's, ordered by name.</summary>
    public IReadOnlyList<AddressSpace> Spaces { get; init; } = [];

    /// <summary>The banks an absolute constant address in each range may be reached from, ordered by address.</summary>
    public IReadOnlyList<AccessRange> Ranges { get; init; } = [];

    /// <summary>
    /// What each named diagnostic is reported as, over the severity the catalogue gives it;
    /// a name mapped to null is not reported at all. An error is never turned down, which is
    /// said where the project file asks for it.
    /// </summary>
    public IReadOnlyDictionary<string, Severity?> Severities { get; init; } = NoSeverities;

    /// <summary>No project: what <c>nt65 build main.nt65</c> works from.</summary>
    public static ProjectSettings None { get; } = new(null, [], null, [], [], []);

    /// <summary>The named configurations, in name order, of which a build chooses one or none.</summary>
    public IReadOnlyList<BuildConfiguration> Configurations { get; init; } = [];

    /// <summary>
    /// The settings the configuration named <paramref name="name"/> builds with: its defines over
    /// the project's, and its <c>out</c> if it gives one. A name the project does not have is
    /// reported at <paramref name="given"/>, and the project's own settings build.
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

    /// <summary>The project's answers about each diagnostic, with <paramref name="over"/> on top.</summary>
    private IReadOnlyDictionary<string, Severity?> Reported(IReadOnlyDictionary<string, Severity?> over)
    {
        if (over.Count == 0)
            return Severities;
        var said = new SortedDictionary<string, Severity?>(StringComparer.Ordinal);
        foreach (var (id, level) in Severities.Concat(over))
            said[id] = level;
        return said;
    }

    /// <summary>The same settings with <paramref name="defines"/> added, overriding by name.</summary>
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
