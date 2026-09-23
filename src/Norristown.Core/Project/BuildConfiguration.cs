using Norristown.Processor;

namespace Norristown.Project;

/// <summary>
/// One of the named configurations in <c>nt65.json</c>, such as <c>debug</c> or <c>pal</c>:
/// the defines it gives, over the project's own, and where its output goes.
/// </summary>
/// <param name="Name">What <c>--config</c> and the editor's setting call it.</param>
/// <param name="Defines">Defines that add to the project's, or override them by name.</param>
/// <param name="Out">Where its output goes, or null to keep the project's.</param>
/// <param name="Declaration">Where the project file names it.</param>
public sealed record BuildConfiguration(string Name, IReadOnlyList<Define> Defines, string? Out, Span Declaration)
{
    /// <summary>The severity it reports each named diagnostic at, overriding the project's own.</summary>
    public IReadOnlyDictionary<string, Severity?> Severities { get; init; } = ProjectSettings.NoSeverities;
}
