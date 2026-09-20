using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A token with its parent and absolute position.</summary>
/// <param name="Parent">The line the token is on.</param>
/// <param name="Green">The green token this one wraps.</param>
/// <param name="Position">Where the token starts in the file's text, trivia included.</param>
public readonly record struct SyntaxToken(SyntaxNode Parent, GreenToken Green, int Position)
{
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

    /// <summary>The syntax diagnostics on this token, in source order.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var result = new List<Diagnostic>();
        Parent.Tree.Collect(Green, Position, result);
        return result;
    }

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;
}
