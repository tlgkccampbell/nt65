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

    /// <summary>The token's range, without trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingWidth, Green.Text.Length);

    /// <summary>The token's range including its trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;
}
