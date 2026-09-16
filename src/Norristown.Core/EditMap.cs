using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// Where the places one version of a file names are in the next. The two texts are compared
/// for what they begin and end with in common: what comes before the difference stays where
/// it was, what comes after it moves by however much longer or shorter the text got, and
/// what is inside it is gone.
/// </summary>
internal sealed class EditMap
{
    private readonly SyntaxTree before;
    private readonly SyntaxTree after;
    private readonly int start;
    private readonly int end;

    public EditMap(SyntaxTree before, SyntaxTree after)
    {
        this.before = before;
        this.after = after;
        var (old, now) = (before.Text, after.Text);
        var shorter = Math.Min(old.Length, now.Length);
        while (start < shorter && old[start] == now[start])
            start++;
        var common = 0;
        while (common < shorter - start && old[old.Length - 1 - common] == now[now.Length - 1 - common])
            common++;
        end = old.Length - common;
    }

    /// <summary>
    /// <paramref name="diagnostic"/> with every place it names in the edited file moved to
    /// where it is now, or null when the edit rewrote one of them.
    /// </summary>
    public Diagnostic? Moved(Diagnostic diagnostic)
    {
        if (diagnostic.Span.File != before.Path && diagnostic.Related.All(related => related.Span.File != before.Path))
            return diagnostic;
        if (Moved(diagnostic.Span) is not { } span)
            return null;
        var related = new List<RelatedSpan>();
        foreach (var other in diagnostic.Related)
        {
            if (Moved(other.Span) is not { } moved)
                return null;
            related.Add(other with { Span = moved });
        }
        return diagnostic with { Span = span, Related = related };
    }

    /// <summary>Every diagnostic of <paramref name="diagnostics"/> moved, or null when one could not be.</summary>
    public List<Diagnostic>? Moved(IEnumerable<Diagnostic> diagnostics)
    {
        var kept = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (Moved(diagnostic) is not { } moved)
                return null;
            kept.Add(moved);
        }
        return kept;
    }

    /// <summary>
    /// What each file but <paramref name="skipped"/> found, moved; or null when anything could
    /// not be.
    /// </summary>
    public Dictionary<string, IReadOnlyList<Diagnostic>>? Moved(
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile, string skipped)
    {
        var kept = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var (file, found) in byFile)
        {
            if (file == skipped)
                continue;
            if (Moved(found) is not { } moved)
                return null;
            kept[file] = moved;
        }
        return kept;
    }

    private Span? Moved(Span span)
    {
        if (span.File != before.Path)
            return span;
        if (span.Line < 1 || span.Line > before.LineStarts.Length)
            return null;
        var from = before.LineStarts[span.Line - 1] + span.StartColumn - 1;
        var length = span.EndColumn - span.StartColumn;
        if (from + length <= start)
            return span;
        if (from < end)
            return null;
        return after.GetSpan(new TextSpan(from + after.Text.Length - before.Text.Length, length));
    }
}
