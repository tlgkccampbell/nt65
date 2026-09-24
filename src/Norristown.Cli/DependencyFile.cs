using System.Text;

namespace Norristown.Cli;

/// <summary>
/// Formats make-style dependencies, as <c>--depfile</c> writes them. There is one rule per output
/// naming the files it depends on, and an empty rule for each of those files, so that make does
/// not stop when a source is deleted.
/// </summary>
public static class DependencyFile
{
    /// <summary>
    /// Returns the rules for <paramref name="targets"/>, each with the files it depends on, in the
    /// order given.
    /// </summary>
    public static string Write(IEnumerable<(string Target, IEnumerable<string> Dependencies)> targets)
    {
        var text = new StringBuilder();
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (target, dependencies) in targets)
        {
            text.Append(Escaped(target)).Append(':');
            foreach (var dependency in dependencies)
            {
                text.Append(" \\\n  ").Append(Escaped(dependency));
                named.Add(dependency);
            }
            text.Append('\n');
        }
        foreach (var dependency in named)
            text.Append('\n').Append(Escaped(dependency)).Append(":\n");
        return text.ToString();
    }

    /// <summary>
    /// Returns a path in the form make reads, with each space, <c>#</c> and <c>$</c> escaped.
    /// </summary>
    private static string Escaped(string path) =>
        path.Replace(" ", "\\ ", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal);
}
