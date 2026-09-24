namespace Norristown;

/// <summary>
/// Measures how near one name is to another, so that a message about a name nothing declares can
/// say what was probably meant, as in <c>`COUNTR` is not declared; `COUNTER` is</c>. The editor's
/// fix and the message come from the same result, so a CLI or CI user is told what an editor
/// would have offered.
/// </summary>
public static class Spelling
{
    /// <summary>
    /// Returns the candidate in <paramref name="candidates"/> that <paramref name="typed"/> most
    /// nearly matches, or null when no candidate differs from it by only a letter or two. A short
    /// name has to match more closely than a long one, because most short names are within two
    /// letters of each other. When two candidates are equally near, the ordinally earlier one
    /// wins, so the same file always gives the same suggestion.
    /// </summary>
    public static string? Nearest(string typed, IEnumerable<string> candidates)
    {
        // How many single-letter changes still count as nearly the same name.
        var allowed = typed.Length <= 4 ? 1 : 2;
        string? nearest = null;
        var best = allowed + 1;
        foreach (var name in candidates)
        {
            if (name == typed)
                continue;
            var distance = Distance(typed, name, allowed + 1);
            if (distance > allowed || distance > best)
                continue;
            if (distance < best || string.CompareOrdinal(name, nearest) < 0)
            {
                nearest = name;
                best = distance;
            }
        }
        return nearest;
    }

    /// <summary>
    /// Returns how many single-letter changes apart two names are, counting no further than
    /// <paramref name="bound"/>. Past that bound the names are not near each other, and how far
    /// past it they are makes no difference.
    /// </summary>
    public static int Distance(string a, string b, int bound)
    {
        if (Math.Abs(a.Length - b.Length) >= bound)
            return bound;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var least = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitute);
                least = Math.Min(least, current[j]);
            }
            if (least >= bound)
                return bound;
            (previous, current) = (current, previous);
        }
        return Math.Min(previous[b.Length], bound);
    }
}
