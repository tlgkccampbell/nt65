namespace Norristown.Emit;

/// <summary>
/// What a line map says: the sources one generated <c>.s</c> was written from, and which line
/// of which of them each of its lines came from.
/// </summary>
/// <param name="Sources">The sources, by the number the map gives each one.</param>
/// <param name="Lines">
/// For each mapped line of the <c>.s</c>, counting from 1, the source it came from and the line
/// of it. A line of the <c>.s</c> that is not here came from nowhere a debugger should name.
/// </param>
public sealed record SourceLines(
    IReadOnlyList<(string Path, int Size)> Sources,
    IReadOnlyDictionary<int, (int File, int Line)> Lines);
