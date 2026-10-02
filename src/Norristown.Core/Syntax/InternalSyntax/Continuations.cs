using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Joins the lines the lexer reads, one per line of the file, into the lines the parser reads. A
/// line continues onto the next while a <c>(</c> or <c>[</c> on it is still open, so a long
/// expression can be written across several lines. The line break becomes trivia on the token
/// before it, and the joined line is lexed text like any other, which the parser reads as one.
/// <para>
/// Only an expression's brackets, a call's or a macro call's arguments and a macro's or a
/// function's parameters may hold a line break, which the parser checks. Joining is decided from
/// the tokens alone, so a line that the parser will refuse is still joined, and the parser says
/// why. To keep an unclosed bracket from swallowing the rest of the file, a line that starts a
/// statement of its own is never joined to the one before it. Such a line is blank, or starts with
/// <c>}</c>, a directive, an instruction, a macro call, a label or a constant. A line holding only
/// a comment is joined, so the parts of a long expression can be explained.
/// </para>
/// <para>
/// Two shapes are joined only in some places, and only the innermost open bracket decides where.
/// A named argument or a parameter's default, <c>count = 3</c>, starts the way a constant does,
/// and is joined where that bracket opens a call's or a macro call's arguments or a list of
/// parameters. A parameter's kind, <c>count: const</c>, starts the way a label does, and is joined
/// where that bracket opens a list of parameters and a word or a name follows the <c>:</c>.
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
        var open = new Stack<BracketKind>();
        for (var i = 0; i < physical.Length;)
        {
            var first = i;
            open.Clear();
            Track(physical[i], open);
            i++;
            while (open.Count > 0 && i < physical.Length && Joins(physical[i], open.Peek()))
            {
                Track(physical[i], open);
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
    /// Updates <paramref name="open"/>, the brackets open, with the brackets on
    /// <paramref name="line"/>. Each bracket is held as the kind of list it opens. A closing
    /// bracket with none open is left for the parser to report.
    /// </summary>
    private static void Track(GreenLine line, Stack<BracketKind> open)
    {
        var tokens = line.Tokens;
        for (var i = 0; i < tokens.Length; i++)
        {
            var kind = tokens[i].Kind;
            if (kind is SyntaxKind.OpenParen or SyntaxKind.OpenBracket)
                open.Push(kind == SyntaxKind.OpenParen ? Opens(tokens, i) : BracketKind.Expression);
            else if (kind is SyntaxKind.CloseParen or SyntaxKind.CloseBracket && open.Count > 0)
                open.Pop();
        }
    }

    /// <summary>
    /// Returns the kind of list the <c>(</c> at <paramref name="at"/> opens. It opens a macro
    /// call's arguments after <c>name!</c>, and a list of parameters after the name that
    /// <c>.macro</c> or <c>.func</c> declares, whatever comes before the directive. After any
    /// other identifier it opens a call's arguments, since a name directly before a <c>(</c> is
    /// read as a call wherever it is not the first token of the line, and at the start of a
    /// line it is a call or a mistake. An instruction's <c>lda (ptr),y</c> has a mnemonic
    /// before its <c>(</c>, not an identifier.
    /// </summary>
    private static BracketKind Opens(ImmutableArray<GreenToken> tokens, int at)
    {
        if (at < 1)
            return BracketKind.Expression;
        if (at >= 2 && Lines.IsMacroCall(tokens, at - 2))
            return BracketKind.MacroArguments;
        if (at >= 2 && Lines.IsName(tokens[at - 1].Kind)
            && tokens[at - 2].DirectiveKind is DirectiveKind.Macro or DirectiveKind.Func)
        {
            return BracketKind.Parameters;
        }
        return tokens[at - 1].Kind == SyntaxKind.Identifier ? BracketKind.Arguments : BracketKind.Expression;
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="line"/> may continue the line before
    /// it: it holds only a comment, or it starts with what may stand inside the innermost bracket
    /// open.
    /// </summary>
    /// <param name="line">The line that may continue the one before it.</param>
    /// <param name="innermost">
    /// The kind of list the innermost open bracket opens. In a call's or a macro call's arguments
    /// a line may also start with a named argument, and in a list of parameters with a
    /// parameter's kind or its default.
    /// </param>
    private static bool Joins(GreenLine line, BracketKind innermost) => line.LineKind switch
    {
        LineKind.Blank => line.Tokens[0].LeadingTrivia.Any(trivia => trivia.Kind == SyntaxKind.CommentTrivia),

        // A built-in function and the `.mod` and `.in` operators are spelled as directives, but
        // are not the directives that start a statement.
        LineKind.Directive => line.Tokens[0].DirectiveKind == DirectiveKind.None,
        LineKind.Expression or LineKind.BareIdentifier => true,

        // `count = 3` is a named argument or a default there. `?=` names neither, so it stays a
        // setting.
        LineKind.Constant => innermost is BracketKind.Arguments or BracketKind.MacroArguments or BracketKind.Parameters
            && line.Tokens[1].Kind == SyntaxKind.Equals,

        // `count: const` gives a parameter's kind there. A kind is a word or an enum's name, so a
        // label followed by an instruction, a directive or nothing still starts a statement.
        LineKind.Label => innermost == BracketKind.Parameters && Lines.IsName(line.Tokens[0].Kind)
            && line.Tokens[2].Kind is SyntaxKind.Identifier or SyntaxKind.ColonColon,
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

    /// <summary>Specifies the kind of list an open bracket holds, which decides what may start a line inside it.</summary>
    private enum BracketKind
    {
        /// <summary>
        /// The brackets of an expression, which are a group, a built-in function's arguments, a set
        /// or an index.
        /// </summary>
        Expression,

        /// <summary>A call's arguments, after the name of the <c>.func</c> or charmap it calls.</summary>
        Arguments,

        /// <summary>A macro call's arguments, after <c>name!</c>.</summary>
        MacroArguments,

        /// <summary>The parameters of a <c>.macro</c> or a <c>.func</c>.</summary>
        Parameters,
    }
}
