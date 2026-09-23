using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// Maps positions in one version of a file to the same positions in the next version. The two
/// texts are compared for the prefix and suffix they share: a position before the changed
/// region stays where it was, a position after it shifts by however much longer or shorter the
/// text got, and a position inside it has no counterpart.
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
    /// A function that moves a diagnostic through every one of <paramref name="maps"/> (each for its
    /// own file), returning null when any map cannot move it.
    /// </summary>
    public static Func<Diagnostic, Diagnostic?> Composed(IReadOnlyList<EditMap> maps) => diagnostic =>
    {
        foreach (var map in maps)
        {
            if (map.Moved(diagnostic) is not { } moved)
                return null;
            diagnostic = moved;
        }
        return diagnostic;
    };

    /// <summary>
    /// Every diagnostic in <paramref name="diagnostics"/> moved by <paramref name="moved"/>, or
    /// null when any one of them could not be moved.
    /// </summary>
    public static List<Diagnostic>? Moved(IEnumerable<Diagnostic> diagnostics, Func<Diagnostic, Diagnostic?> moved)
    {
        var kept = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (moved(diagnostic) is not { } now)
                return null;
            kept.Add(now);
        }
        return kept;
    }

    /// <summary>
    /// <paramref name="diagnostic"/> with every place it names in the edited file moved to
    /// where it is now, or null when the edit rewrote one of them.
    /// </summary>
    public Diagnostic? Moved(Diagnostic diagnostic)
    {
        if (diagnostic.Span.File != before.Path && diagnostic.Related.All(related => related.Span.File != before.Path)
            && diagnostic.Fix?.At?.File != before.Path)
        {
            return diagnostic;
        }
        if (Moved(diagnostic.Span) is not { } span)
            return null;
        var related = new List<RelatedSpan>();
        foreach (var other in diagnostic.Related)
        {
            if (Moved(other.Span) is not { } moved)
                return null;
            related.Add(other with { Span = moved });
        }
        var fix = diagnostic.Fix;
        if (fix?.At is { } at)
        {
            if (Moved(at) is not { } movedAt)
                return null;
            fix = fix with { At = movedAt };
        }
        return diagnostic with { Span = span, Related = related, Fix = fix };
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
