namespace Norristown.Emit;

/// <summary>
/// Represents the contents of a line map. It lists the sources one generated <c>.s</c> was
/// emitted from, gives, for each line of the <c>.s</c>, the source and line it came from, and
/// gives the source path of each value the <c>.s</c> defines.
/// </summary>
/// <param name="Sources">The sources, by the number the map gives each one.</param>
/// <param name="Lines">
/// For each mapped line of the <c>.s</c>, counting from 1, the source it came from and the line
/// in it. A line of the <c>.s</c> that is missing here has no source line a debugger should show.
/// </param>
/// <param name="Names">
/// The source path of each value the <c>.s</c> defines, such as <c>wave::shown</c>, keyed by the
/// name the <c>.s</c> gives it, such as <c>shown</c>.
/// </param>
public sealed record SourceLines(
    IReadOnlyList<(string Path, int Size)> Sources,
    IReadOnlyDictionary<int, (int File, int Line)> Lines,
    IReadOnlyDictionary<string, string> Names);
