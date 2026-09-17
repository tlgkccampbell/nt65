using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The line at the caret as far as the caret, lexed on its own. What is being written there is
/// read off these tokens rather than off the parsed file, because a line being typed is seldom
/// one that parses.
/// </summary>
internal sealed class LineContext
{
    private LineContext(IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> tokens, int caret, bool partial)
    {
        Caret = caret;
        if (partial)
        {
            Partial = tokens[^1];
            Before = [.. tokens.Take(tokens.Count - 1)];
        }
        else
        {
            Before = tokens;
        }
    }

    /// <summary>The caret, as a position in the file.</summary>
    public int Caret { get; }

    /// <summary>The name the caret is at the end of, which a completion replaces, or null.</summary>
    public (SyntaxKind Kind, string Text, int Start)? Partial { get; }

    /// <summary>The tokens before that name, or before the caret when there is none.</summary>
    public IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> Before { get; }

    /// <summary>Where a completion's text goes: over the name being typed, or at the caret.</summary>
    public TextSpan Replaced => Partial is { } partial ? new TextSpan(partial.Start, Caret - partial.Start) : new TextSpan(Caret, 0);

    /// <summary>
    /// The directive the statement starts with, lower case, past an <c>.export</c> or a label;
    /// null for a statement that starts with none.
    /// </summary>
    public string? Directive
    {
        get
        {
            var at = Start;
            return at < Before.Count && Before[at].Kind == SyntaxKind.Directive ? Before[at].Text.ToLowerInvariant() : null;
        }
    }

    /// <summary>The index of the statement's first token, past an <c>.export</c> and a label.</summary>
    public int Start
    {
        get
        {
            var at = 0;
            if (at + 1 < Before.Count && Before[at].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                && Before[at + 1].Kind == SyntaxKind.Colon)
            {
                at += 2;
            }
            if (at < Before.Count && Before[at] is { Kind: SyntaxKind.Directive } export
                && export.Text.Equals(".export", StringComparison.OrdinalIgnoreCase))
            {
                at++;
            }
            return at;
        }
    }

    /// <summary>The line of <paramref name="tree"/> up to <paramref name="position"/>.</summary>
    public static LineContext At(SyntaxTree tree, int position)
    {
        var lineStart = tree.LineStarts[tree.GetLineIndex(position)];
        var tokens = Lexed(tree, lineStart, position);

        // A name that runs right up to the caret is the one being typed.
        var partial = tokens.Count > 0
            && tokens[^1].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Mnemonic
                or SyntaxKind.Register or SyntaxKind.Directive
            && tokens[^1].Start + tokens[^1].Text.Length == position;
        return new LineContext(tokens, position, partial);
    }

    /// <summary>
    /// The call the caret is in the arguments of, innermost first: the index of the <c>(</c> that
    /// opens them, and how many arguments come before the caret's. Null outside every call.
    /// <paramref name="end"/> looks from before that token instead of the caret, for the call
    /// around one.
    /// </summary>
    public (int Open, int Argument)? OpenCall(int? end = null)
    {
        var depth = 0;
        var commas = 0;
        for (var i = (end ?? Before.Count) - 1; i >= 0; i--)
        {
            switch (Before[i].Kind)
            {
                case SyntaxKind.CloseParen or SyntaxKind.CloseBracket or SyntaxKind.CloseBrace:
                    depth++;
                    break;
                case SyntaxKind.OpenParen or SyntaxKind.OpenBracket or SyntaxKind.OpenBrace when depth > 0:
                    depth--;
                    break;
                case SyntaxKind.OpenParen:
                    return (i, commas);
                case SyntaxKind.OpenBracket or SyntaxKind.OpenBrace:
                    return null;
                case SyntaxKind.Comma when depth == 0:
                    commas++;
                    break;
                default:
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// The names written before the <c>::</c> the caret follows, <c>hw::vic::</c> being
    /// <c>hw</c> and <c>vic</c>, or null when the caret follows no <c>::</c>.
    /// </summary>
    public IReadOnlyList<string>? PathBefore(int end)
    {
        if (end == 0 || Before[end - 1].Kind != SyntaxKind.ColonColon)
            return null;
        var parts = new List<string>();
        var at = end - 1;
        while (at >= 1 && Before[at].Kind == SyntaxKind.ColonColon && IsWord(Before[at - 1].Kind))
        {
            parts.Insert(0, Before[at - 1].Text);
            at -= 2;
        }
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>A whole line's tokens, but for its line break, with where each starts in the file.</summary>
    public static List<(SyntaxKind Kind, string Text, int Start)> TokensOf(SyntaxTree tree, int line) =>
        Lexed(tree, tree.LineStarts[line], line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length);

    /// <summary>Where the code on a line ends, before any comment: its start, for a line with none.</summary>
    public static int CodeEnd(SyntaxTree tree, int line) =>
        TokensOf(tree, line) is [.., var last] ? last.Start + last.Text.Length : tree.LineStarts[line];

    /// <summary>Whether a token is one a name can be written as.</summary>
    public static bool IsWord(SyntaxKind kind) =>
        kind is SyntaxKind.Identifier or SyntaxKind.Mnemonic or SyntaxKind.Register;

    private static List<(SyntaxKind Kind, string Text, int Start)> Lexed(SyntaxTree tree, int start, int end)
    {
        var tokens = new List<(SyntaxKind Kind, string Text, int Start)>();
        var at = start;
        foreach (var token in Lexer.LexLine(tree.Text.AsSpan(start, end - start)).Tokens)
        {
            if (token.Kind != SyntaxKind.EndOfLine)
                tokens.Add((token.Kind, token.Text, at + token.LeadingWidth));
            at += token.FullWidth;
        }
        return tokens;
    }
}
