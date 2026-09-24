using Norristown.Processor;

namespace Norristown.Project;

/// <summary>
/// Represents one of the named configurations in <c>nt65.json</c>, such as <c>debug</c> or
/// <c>pal</c>. A configuration gives defines over the project's own, and says where its output
/// goes.
/// </summary>
/// <param name="Name">The name that <c>--config</c> and the editor's setting use for the configuration.</param>
/// <param name="Defines">Defines that add to the project's defines, or override them by name.</param>
/// <param name="Out">The directory the configuration's output goes to, or null to keep the project's.</param>
/// <param name="Declaration">Where the project file names the configuration.</param>
public sealed record BuildConfiguration(string Name, IReadOnlyList<Define> Defines, string? Out, Span Declaration)
{
    /// <summary>
    /// Gets the severity at which the configuration reports each named diagnostic, overriding
    /// the project's own.
    /// </summary>
    public IReadOnlyDictionary<string, Severity?> Severities { get; init; } = ProjectSettings.NoSeverities;
}
