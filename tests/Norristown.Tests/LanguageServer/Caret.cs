using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Reads a test source in which <c>|</c> marks the caret. A test that writes the caret into
/// its source, instead of counting lines and columns, keeps probing the same place when the
/// source is edited.
/// </summary>
internal static class Caret
{
    /// <summary>
    /// Returns <paramref name="source"/> with its <c>|</c> removed and the position the
    /// <c>|</c> was at. The source must contain exactly one <c>|</c>.
    /// </summary>
    public static (string Text, Position Position) In(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
        var marked = lines.FindAll(line => line.Contains('|', StringComparison.Ordinal));
        if (marked.Count != 1 || marked[0].IndexOf('|', StringComparison.Ordinal) != marked[0].LastIndexOf('|'))
            throw new ArgumentException("the source must mark the caret with exactly one |", nameof(source));
        var at = lines.IndexOf(marked[0]);
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Remove(column, 1);
        return (string.Join('\n', lines), new Position(at, column));
    }
}
