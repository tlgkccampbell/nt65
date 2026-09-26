using System.Collections.Frozen;
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
/// <param name="SettingValues">The values the build gives settings, together with any that <c>-D</c> added or overrode.</param>
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
    IReadOnlyList<SettingValue> SettingValues,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The severities of a project that overrides no diagnostic, which is an empty map.</summary>
    public static readonly IReadOnlyDictionary<string, Severity?> NoSeverities =
        FrozenDictionary<string, Severity?>.Empty;

    /// <summary>Gets the settings for no project, which <c>nt65 build main.nt65</c> works from.</summary>
    public static ProjectSettings None { get; } = new(null, [], null, [], [], []);

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

    /// <summary>Gets the named configurations in name order. A build chooses one of them or none.</summary>
    public IReadOnlyList<BuildConfiguration> Configurations { get; init; } = [];

    /// <summary>
    /// Gets the configuration a build uses when it chooses none, or null when such a build uses
    /// the project's own settings. A project names one when its own settings cannot build, such
    /// as when each configuration targets a different machine.
    /// </summary>
    public BuildConfiguration? Default { get; init; }

    /// <summary>
    /// Gets the linker configs the build is linked with, in name order. When there are any, they
    /// declare the program's segments.
    /// </summary>
    public IReadOnlyList<Link> Links { get; init; } = [];

    /// <summary>
    /// Gets the entries of the project file's <c>segments</c> as it gives them, from which
    /// <see cref="Segments"/> is built with the links.
    /// </summary>
    public IReadOnlyList<ProjectSegment> SegmentEntries { get; init; } = [];

    /// <summary>
    /// Gets the logical paths of every linker config the project links, in any configuration. A
    /// change to one of them changes the project.
    /// </summary>
    public IEnumerable<string> LinkedFiles =>
        Links.Concat(Configurations.SelectMany(configuration => configuration.Links ?? []))
            .Select(link => link.ConfigPath)
            .Distinct(StringComparer.Ordinal);

    // The problems with the project file itself, to which the problems with its links are added.
    private IReadOnlyList<Diagnostic> FileDiagnostics { get; init; } = [];

    /// <summary>
    /// Returns the settings that the configuration named <paramref name="name"/> builds with,
    /// which are its setting values over the project's, and its <c>out</c> if it gives one. If the
    /// project has no configuration with that name, reports an error at <paramref name="given"/>
    /// and uses the project's own settings.
    /// </summary>
    public ProjectSettings Configured(string name, Span given)
    {
        if (Configurations.FirstOrDefault(configuration => configuration.Name == name) is { } chosen)
        {
            var configured = With(chosen.SettingValues) with
            {
                Out = chosen.Out ?? Out,
                Severities = Reported(chosen.Severities),
            };
            if (chosen.Links is not { } links)
                return configured;
            var byName = Links.ToDictionary(link => link.Name, StringComparer.Ordinal);
            foreach (var link in links)
                byName[link.Name] = link;
            return configured.Linked([.. byName.Values.OrderBy(link => link.Name, StringComparer.Ordinal)], FileDiagnostics);
        }
        var message = Catalogue.ConfigurationUnknown.Message(name, Listed(Configurations));
        return this with { Diagnostics = [.. Diagnostics, new Diagnostic(given, message)] };
    }

    /// <summary>
    /// Returns the settings a build uses when it chooses no configuration, which are those of the
    /// <see cref="Default"/> configuration when the project names one, and its own otherwise.
    /// </summary>
    public ProjectSettings Defaulted() => Default is { } chosen ? Configured(chosen.Name, chosen.Declaration) : this;

    /// <summary>
    /// Returns these settings with <paramref name="values"/> added, each replacing any existing
    /// value given under the same name.
    /// </summary>
    public ProjectSettings With(IReadOnlyList<SettingValue> values)
    {
        if (values.Count == 0)
            return this;
        var byName = SettingValues.ToDictionary(value => value.Name, StringComparer.Ordinal);
        foreach (var value in values)
            byName[value.Name] = value;
        return this with { SettingValues = [.. byName.Values.OrderBy(value => value.Name, StringComparer.Ordinal)] };
    }

    /// <summary>
    /// Returns the phrase that lists the names of <paramref name="configurations"/> in a message
    /// about a name that is not among them.
    /// </summary>
    internal static string Listed(IReadOnlyList<BuildConfiguration> configurations)
    {
        var named = configurations.Select(configuration => $"`{configuration.Name}`").ToList();
        return named.Count == 0
            ? $"{ProjectFile.Name} names none"
            : $"{ProjectFile.Name} names "
                + (named.Count == 1 ? named[0] : string.Join(", ", named.SkipLast(1)) + " and " + named[^1]);
    }

    /// <summary>
    /// Returns these settings linked with <paramref name="links"/>, with the segments they and
    /// the project file's <c>segments</c> declare. The problems found are reported after
    /// <paramref name="fileDiagnostics"/>, the problems with the project file itself.
    /// </summary>
    internal ProjectSettings Linked(IReadOnlyList<Link> links, IReadOnlyList<Diagnostic> fileDiagnostics)
    {
        var diagnostics = new List<Diagnostic>(fileDiagnostics);
        var segments = SegmentLinks.Resolve(SegmentEntries, links, Spaces, diagnostics);
        return this with
        {
            Links = links,
            Segments = segments,
            FileDiagnostics = fileDiagnostics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Returns the project's severity for each diagnostic, with the entries in
    /// <paramref name="over"/> taking precedence.
    /// </summary>
    private IReadOnlyDictionary<string, Severity?> Reported(IReadOnlyDictionary<string, Severity?> over)
    {
        if (over.Count == 0)
            return Severities;
        var merged = new SortedDictionary<string, Severity?>(StringComparer.Ordinal);
        foreach (var (id, level) in Severities.Concat(over))
            merged[id] = level;
        return merged;
    }
}
