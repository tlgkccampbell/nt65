using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Joins the lines the lexer reads, one per line of the file, into the lines the parser reads. A
/// line continues onto the next while a <c>(</c> or <c>[</c> on it is still open, so a long
/// expression can be written across several lines. The line break becomes trivia on the token
/// before it, and the joined line is lexed text like any other, which the parser reads as one.
/// <para>
/// Only an expression's brackets may hold a line break, which the parser checks. Joining is
/// decided from the tokens alone, so a line that the parser will refuse is still joined, and the
/// parser says why. To keep an unclosed bracket from swallowing the rest of the file, a line that
/// starts a statement of its own is never joined to the one before it. Such a line is blank, or
/// starts with <c>}</c>, a directive, an instruction, a macro call, a label or a constant. A
/// line holding only a comment is joined, so the parts of a long expression can be explained.
/// </para>
/// </summary>
internal static class Continuations
{
    /// <summary>
    /// Returns the lines the parser reads, each one line of the file or several joined, and
    /// where each starts. Where a joined line has the same parts as one in
    /// <paramref name="previous"/>, that line is used again, so it keeps its parse.
    /// </summary>
    /// <param name="physical">The lines the lexer read, one per line of the file.</param>
    /// <param name="previous">The lines of the tree before an edit, or empty.</param>
    /// <param name="firsts">For each line returned, the index in <paramref name="physical"/> where it starts.</param>
    public static ImmutableArray<GreenLine> Join(
        ImmutableArray<GreenLine> physical, ImmutableArray<GreenLine> previous, out ImmutableArray<int> firsts)
    {
        var lines = ImmutableArray.CreateBuilder<GreenLine>(physical.Length);
        var starts = ImmutableArray.CreateBuilder<int>(physical.Length);
        Dictionary<GreenLine, GreenLine>? joinedBefore = null;
        for (var i = 0; i < physical.Length;)
        {
            var first = i;
            var depth = 0;
            Track(physical[i], ref depth);
            i++;
            while (depth > 0 && i < physical.Length && Joins(physical[i]))
            {
                Track(physical[i], ref depth);
                i++;
            }
            starts.Add(first);
            if (i - first == 1)
            {
                lines.Add(physical[first]);
                continue;
            }

            var parts = physical.AsSpan(first, i - first);
            joinedBefore ??= JoinedIn(previous);
            lines.Add(joinedBefore.TryGetValue(physical[first], out var before) && before.Parts.AsSpan().SequenceEqual(parts)
                ? before
                : Joined(parts));
        }
        firsts = starts.ToImmutable();
        return lines.ToImmutable();
    }

    /// <summary>
    /// Updates <paramref name="depth"/>, the number of brackets open, with the brackets on
    /// <paramref name="line"/>. A closing bracket with none open is left for the parser to report.
    /// </summary>
    private static void Track(GreenLine line, ref int depth)
    {
        foreach (var token in line.Tokens)
        {
            if (token.Kind is SyntaxKind.OpenParen or SyntaxKind.OpenBracket)
                depth++;
            else if (token.Kind is SyntaxKind.CloseParen or SyntaxKind.CloseBracket && depth > 0)
                depth--;
        }
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="line"/> may continue the line before
    /// it: it holds only a comment, or it starts with what may stand inside an expression.
    /// </summary>
    private static bool Joins(GreenLine line) => line.LineKind switch
    {
        LineKind.Blank => line.Tokens[0].LeadingTrivia.Any(trivia => trivia.Kind == SyntaxKind.CommentTrivia),

        // A built-in function and the `.mod` and `.in` operators are spelled as directives, but
        // are not the directives that start a statement.
        LineKind.Directive => line.Tokens[0].DirectiveKind == DirectiveKind.None,
        LineKind.Expression or LineKind.BareIdentifier => true,
        _ => false,
    };

    /// <summary>Returns the joined lines of <paramref name="lines"/>, by their first part.</summary>
    private static Dictionary<GreenLine, GreenLine> JoinedIn(ImmutableArray<GreenLine> lines)
    {
        var joined = new Dictionary<GreenLine, GreenLine>(ReferenceEqualityComparer.Instance);
        foreach (var line in lines.IsDefault ? [] : lines)
        {
            if (line.Parts.Length > 1)
                joined[line.Parts[0]] = line;
        }
        return joined;
    }

    /// <summary>
    /// Returns one line holding the tokens of <paramref name="parts"/>. The line break of every
    /// part but the last becomes trailing trivia of the token before it, and a part that holds
    /// only a comment adds its comment and its line break there too.
    /// </summary>
    private static GreenLine Joined(ReadOnlySpan<GreenLine> parts)
    {
        var tokens = ImmutableArray.CreateBuilder<GreenToken>();
        for (var p = 0; p < parts.Length; p++)
        {
            var line = parts[p].Tokens;
            var end = line[^1];
            if (p == parts.Length - 1)
            {
                tokens.AddRange(line);
                break;
            }
            for (var t = 0; t < line.Length - 1; t++)
                tokens.Add(line[t]);

            // The first part always has a token before its line break: its open bracket.
            var before = tokens[^1];
            tokens[^1] = WithTrailing(before, [.. end.LeadingTrivia, new GreenTrivia(SyntaxKind.LineBreakTrivia, end.Text)]);
        }
        return new GreenLine(tokens.ToImmutable(), [.. parts]);
    }

    /// <summary>
    /// Returns <paramref name="token"/> with <paramref name="more"/> after its trailing trivia,
    /// keeping any lexical error it has.
    /// </summary>
    private static GreenToken WithTrailing(GreenToken token, ImmutableArray<GreenTrivia> more)
    {
        var extended = new GreenToken(token.Kind, token.Text, token.LeadingTrivia, token.TrailingTrivia.AddRange(more), null);
        foreach (var diagnostic in token.Diagnostics)
            extended.Report(diagnostic);
        return extended;
    }
}
