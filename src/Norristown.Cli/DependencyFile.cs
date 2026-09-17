using System.Text;

namespace Norristown.Cli;

/// <summary>
/// Make-style dependencies, as <c>--depfile</c> writes them: one rule per output naming the
/// files it depends on, and an empty rule for each of those, so that make does not stop when a
/// source is deleted.
/// </summary>
public static class DependencyFile
{
    /// <summary>The rules for <paramref name="targets"/>, each with the files it depends on, in the order given.</summary>
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

    /// <summary>A path as make reads it: a space, a <c>#</c> and a <c>$</c> escaped.</summary>
    private static string Escaped(string path) =>
        path.Replace(" ", "\\ ", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal);
}
