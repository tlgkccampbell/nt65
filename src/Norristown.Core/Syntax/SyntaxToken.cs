using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A token with its parent and absolute position. It is a value of three words, copied rather
/// than allocated, and two of them are equal when they are the same token of the same tree read
/// through the same node. The tokens a tree hands out are the only ones there are: a default
/// <see cref="SyntaxToken"/> is no token of anything, which is what a lookup answers with when
/// there is nothing to answer.
/// </summary>
public readonly record struct SyntaxToken
{
    /// <summary>The token <paramref name="green"/> is, read through <paramref name="parent"/>.</summary>
    /// <param name="parent">The node the token is a piece of.</param>
    /// <param name="green">The green token it wraps.</param>
    /// <param name="position">Where it starts in the file's text, trivia included.</param>
    internal SyntaxToken(SyntaxNode parent, GreenToken green, int position)
    {
        Parent = parent;
        Green = green;
        Position = position;
    }

    /// <summary>The node the token is a piece of, which for a line's own is the line.</summary>
    public SyntaxNode Parent { get; }

    /// <summary>Where the token starts in the file's text, trivia included.</summary>
    public int Position { get; }

    /// <summary>What the token is.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>The token's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>
    /// Whether the token stands where one belongs that the source does not have. Its text is
    /// empty and its span is the empty span where it would have been written.
    /// </summary>
    public bool IsMissing => Green.IsMissing;

    /// <summary>
    /// Whether the token carries a diagnostic: a lexical error over its text, or, on a missing
    /// token, what the line wanted where it stands.
    /// </summary>
    public bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>The token's range, without trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingWidth, Green.Text.Length);

    /// <summary>The token's range including its trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>The whitespace before the token; only the first token on a line has any.</summary>
    public SyntaxTriviaList LeadingTrivia => new(this, Green.LeadingTrivia, Position);

    /// <summary>The whitespace and comment after the token, up to the end of its line.</summary>
    public SyntaxTriviaList TrailingTrivia => new(this, Green.TrailingTrivia, Span.End);

    /// <summary>The green token this one wraps.</summary>
    internal GreenToken Green { get; }

    /// <summary>The token's text with its trivia, exactly as in the source.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>
    /// The token written after this one, wherever it is: the next token of this line, or the
    /// first of the line below, and null at the end of the file. It walks the same tokens as
    /// <see cref="SyntaxNode.DescendantTokens"/>, missing ones included, and each of them belongs
    /// to the node it is part of.
    /// </summary>
    public SyntaxToken? GetNextToken() => Step(1);

    /// <summary>
    /// The token written before this one, wherever it is: the token before it on this line, or
    /// the last of the line above, and null at the start of the file.
    /// </summary>
    public SyntaxToken? GetPreviousToken() => Step(-1);

    /// <summary>The syntax diagnostics on this token, in source order.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var result = new List<Diagnostic>();
        Parent.Tree.Collect(Green, Position, result);
        return result;
    }

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>Whether <paramref name="token"/> is the one written where <paramref name="sought"/> is.</summary>
    private static bool Written(SyntaxToken token, SyntaxToken sought) =>
        token.Position == sought.Position && ReferenceEquals(token.Green, sought.Green);

    /// <summary>
    /// The token one step along from this one, forwards or backwards. A line is the unit walked:
    /// it holds few enough tokens to read them all, and a token asked for past either end of one
    /// is the first or last token of the line next door.
    /// </summary>
    /// <param name="direction">1 for the token after this one, −1 for the one before it.</param>
    private SyntaxToken? Step(int direction)
    {
        // A token of a line that is a piece of a statement belongs to that statement's node, so
        // the line it is written on is the one above it all.
        var owner = Parent;
        while (owner is not LineSyntax && owner.Parent is { } outer)
            owner = outer;

        // Two pieces the source leaves out stand at the same place with nothing between them —
        // `f(g(1` misses two `)` — so which node a token hangs from is part of saying which it
        // is. A token read off a line rather than off the pieces belongs to the line instead,
        // and is the one written at its place.
        var self = this;
        var tokens = owner.DescendantTokens().ToList();
        var at = tokens.FindIndex(token => Written(token, self) && ReferenceEquals(token.Parent, self.Parent));
        if (at < 0)
            at = tokens.FindIndex(token => Written(token, self));
        if (at < 0)
            return null;
        if (at + direction >= 0 && at + direction < tokens.Count)
            return tokens[at + direction];
        if (owner is not LineSyntax line)
            return null;

        var next = line.LineIndex + direction;
        if (next < 0 || next >= line.Tree.LineCount)
            return null;
        var beyond = line.Tree.GetLine(next).DescendantTokens().ToList();
        return beyond.Count == 0 ? null : beyond[direction > 0 ? 0 : ^1];
    }
}
