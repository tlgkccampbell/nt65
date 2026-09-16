using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// What <c>nt65.json</c> says about a program (§5.3). A single file built without one uses
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
    /// <summary>No project: what <c>nt65 build main.nt65</c> works from.</summary>
    public static ProjectSettings None { get; } = new(null, [], null, [], [], []);

    /// <summary>The same settings with <paramref name="defines"/> added, overriding by name (§5.3).</summary>
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
