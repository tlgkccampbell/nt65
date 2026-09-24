using System.Text;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Holds what the output writes in place of a statement's own tokens, and renders the statement
/// with those edits applied. The emitter decides what each edit is. This type only records the
/// edits and applies them to the source's tokens, so it needs nothing about where the walk is.
/// </summary>
internal sealed class TokenRewriter
{
    /// <summary>Gets the text to write before a token, keyed by the token's position.</summary>
    public Dictionary<int, string> Before { get; } = [];

    /// <summary>Gets the text to write after a token, keyed by the token's position.</summary>
    public Dictionary<int, string> After { get; } = [];

    /// <summary>
    /// Gets the text to write instead of a token, keyed by the token's position. An empty
    /// string omits the token.
    /// </summary>
    public Dictionary<int, string> Replacements { get; } = [];

    /// <summary>Gets the source spellings to keep in a comment at the end of the line.</summary>
    public List<string> Comments { get; } = [];

    /// <summary>
    /// Gets the positions of tokens written as part of the token before them, with no
    /// whitespace between the two.
    /// </summary>
    public HashSet<int> Joined { get; } = [];

    /// <summary>
    /// Returns the tokens under a node, in source order. A missing token is nowhere in the text,
    /// so there is nothing of it to write or to blank, and it is left out.
    /// </summary>
    public static List<SyntaxToken> Tokens(SyntaxNode node) =>
        [.. node.DescendantTokens().Where(token => !token.IsMissing)];

    /// <summary>
    /// Converts an operator to ca65's form. ca65 writes equality as `=`, inequality as
    /// `&lt;&gt;` and nt65's `^^` as `.xor`. The operators `&amp;&amp;`, `||` and `!` are the
    /// same in both.
    /// </summary>
    public static string Ca65Operator(SyntaxToken op) => op.Kind switch
    {
        SyntaxKind.EqualsEquals => "=",
        SyntaxKind.BangEquals => "<>",

        // ca65 has no `^^`: it reads `a ^^ b` as `a ^ ^b`, an xor with the bank byte.
        SyntaxKind.CaretCaret => ".xor",
        _ => op.Text,
    };

    /// <summary>
    /// Returns the statement's own text with the edits applied. The text keeps the source's
    /// spacing between tokens, drops its comments, and has names, prefixes and byte values
    /// written where the source had something else. The source's indentation is not kept, and
    /// the caller decides where the line goes with <paramref name="indent"/>.
    /// </summary>
    public string Render(SyntaxNode statement, string indent = "")
    {
        var text = new StringBuilder();
        var tokens = Tokens(statement);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.EndOfLine)
                continue;
            if (Joined.Contains(token.Position))
            {
                if (!Joined.Contains(tokens[i - 1].Position))
                    text.Length -= WhitespaceWidth(tokens[i - 1].TrailingTrivia);
                text.Append(Before.GetValueOrDefault(token.Position, ""))
                    .Append(Replacements.GetValueOrDefault(token.Position, Format(token)))
                    .Append(After.GetValueOrDefault(token.Position, ""));
                if (i + 1 == tokens.Count || !Joined.Contains(tokens[i + 1].Position))
                    AppendWhitespace(text, token.TrailingTrivia);
                continue;
            }
            AppendWhitespace(text, token.LeadingTrivia);
            if (Before.TryGetValue(token.Position, out var before))
                text.Append(before);
            text.Append(Replacements.TryGetValue(token.Position, out var replacement) ? replacement : Format(token));
            if (After.TryGetValue(token.Position, out var after))
                text.Append(after);
            AppendWhitespace(text, token.TrailingTrivia);
        }

        var line = indent + text.ToString().Trim();
        return Comments.Count == 0 ? line : EmittedLine.Commented(line, string.Join(", ", Comments));
    }

    /// <summary>
    /// Returns <paramref name="node"/> rendered without the comments the edits collected. Those
    /// comments are returned in <paramref name="comment"/> instead, to go after anything the
    /// line puts in front of the text.
    /// </summary>
    public string Bare(SyntaxNode node, out string? comment)
    {
        comment = Comments.Count == 0 ? null : string.Join(", ", Comments);
        Comments.Clear();
        return Render(node).Trim();
    }

    /// <summary>
    /// Returns <paramref name="node"/> rendered to go inside another line. Its comments belong
    /// to that line and go to <paramref name="comments"/>, or stay on the text when
    /// <paramref name="comments"/> is null.
    /// </summary>
    public string Inline(SyntaxNode node, List<string>? comments)
    {
        if (comments is null)
            return Render(node).Trim();
        comments.AddRange(Comments);
        Comments.Clear();
        return Render(node).Trim();
    }

    /// <summary>
    /// Writes <paramref name="text"/> in place of all of <paramref name="node"/>. Edits already
    /// made before and after the node stay, and edits already made inside it are discarded; with
    /// <paramref name="around"/> false, the edits before its first token and after its last are
    /// discarded too.
    /// </summary>
    public void Replace(SyntaxNode node, string text, bool around = true)
    {
        var tokens = Tokens(node);

        // A missing node, such as the value after a trailing comma, has no tokens to write at.
        if (tokens.Count == 0)
            return;
        for (var i = 0; i < tokens.Count; i++)
        {
            Replacements[tokens[i].Position] = "";
            if (i > 0 || !around)
                Before.Remove(tokens[i].Position);
            if (i < tokens.Count - 1 || !around)
                After.Remove(tokens[i].Position);
        }
        Replacements[tokens[0].Position] = text;

        // The spaces between the tokens it replaces go with them, and the ones around it stay.
        for (var i = 1; i < tokens.Count; i++)
            Joined.Add(tokens[i].Position);
    }

    /// <summary>
    /// Writes <paramref name="text"/> where the whole of <paramref name="name"/> stood, at the
    /// first token of the name itself, with the rest of its tokens blanked. An <c>[i]</c> along
    /// the path is left in place, because it selects an element of what the name refers to
    /// rather than being part of the name.
    /// </summary>
    public void ReplaceName(NameExpressionSyntax name, string text)
    {
        var names = name.Names;
        var first = name.GlobalToken;
        if (first is null)
        {
            if (names.IsEmpty)
                return;
            first = names[0];
        }
        foreach (var token in names)
            Replacements[token.Position] = "";

        // What is left directly under the name are the `::` between its parts.
        foreach (var token in name.ChildTokens)
            Replacements[token.Position] = "";
        Replacements[first.Value.Position] = text;
    }

    /// <summary>
    /// Rewrites a <c>.strz</c> as a <c>.byte</c>. Text reaches the output as bytes, so
    /// <c>.strz</c> becomes the bytes and the zero that ends them. ca65's own directive takes a
    /// string, and once the text has become bytes there is no string left to give it.
    /// </summary>
    public void TerminateStrz(DataDirectiveSyntax directive)
    {
        if (directive.Directive.DirectiveKind != DirectiveKind.Strz)
            return;

        Replacements[directive.Directive.Position] = ".byte";
        var last = directive.Tail is InlineDataSyntax { Values: [.., var argument] } && Tokens(argument) is [.., var token]
            ? token.Position
            : directive.Directive.Position;
        After[last] = After.GetValueOrDefault(last, "") + ", $00";
    }

    /// <summary>
    /// Formats a token for the output. Every token but a number is kept as it appears in the
    /// source. Everything nt65 works out for itself is written in lower case
    /// (<see cref="Ca65Numbers.Hex"/>), so a hexadecimal number in upper case in the source is
    /// lowered to match. Otherwise one file with <c>$FFD2</c> in one line and <c>$d020</c> in the
    /// next would look as if two people wrote it. The <c>_</c> that separates a number's digits is
    /// nt65's own, and the header switches ca65's <c>underline_in_numbers</c> off, so it is
    /// dropped on the way out.
    /// </summary>
    private static string Format(SyntaxToken token)
    {
        if (token.Kind != SyntaxKind.NumberLiteral)
            return token.Text;
        var text = token.Text.Replace("_", "", StringComparison.Ordinal);
        return text.StartsWith('$') ? text.ToLowerInvariant() : text;
    }

    /// <summary>Returns the number of whitespace characters in a token's trivia.</summary>
    private static int WhitespaceWidth(SyntaxTriviaList trivia) =>
        trivia.Where(piece => piece.Kind == SyntaxKind.WhitespaceTrivia).Sum(piece => piece.Text.Length);

    /// <summary>Appends the whitespace in a token's trivia, leaving out its comments.</summary>
    private static void AppendWhitespace(StringBuilder text, SyntaxTriviaList trivia)
    {
        foreach (var piece in trivia)
        {
            if (piece.Kind == SyntaxKind.WhitespaceTrivia)
                text.Append(piece.Text);
        }
    }
}
