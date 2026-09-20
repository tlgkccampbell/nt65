using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Whitespace or a comment, with the token it belongs to and its place in the file.</summary>
/// <param name="Token">The token the trivia sits beside.</param>
/// <param name="Green">The green trivia this one wraps.</param>
/// <param name="Position">Where the trivia starts in the file's text.</param>
public readonly record struct SyntaxTrivia(SyntaxToken Token, GreenTrivia Green, int Position)
{
    /// <summary>Whether this is whitespace or a comment.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>The trivia's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>The trivia's range in the file's text.</summary>
    public TextSpan Span => new(Position, Green.Text.Length);

    /// <summary>The trivia's text.</summary>
    public override string ToString() => Text;
}
