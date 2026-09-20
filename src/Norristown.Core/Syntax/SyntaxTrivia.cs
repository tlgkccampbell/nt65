using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Whitespace or a comment, with the token it belongs to and its place in the file.</summary>
public readonly record struct SyntaxTrivia
{
    internal SyntaxTrivia(SyntaxToken token, GreenTrivia green, int position)
    {
        Token = token;
        Green = green;
        Position = position;
    }

    /// <summary>The token the trivia sits beside.</summary>
    public SyntaxToken Token { get; }

    /// <summary>Where the trivia starts in the file's text.</summary>
    public int Position { get; }

    /// <summary>Whether this is whitespace or a comment.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>The trivia's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>The trivia's range in the file's text.</summary>
    public TextSpan Span => new(Position, Green.Text.Length);

    /// <summary>
    /// The trivia's range, which is the same as <see cref="Span"/>: trivia is all text and has
    /// nothing around it.
    /// </summary>
    public TextSpan FullSpan => Span;

    /// <summary>The green trivia this one wraps.</summary>
    internal GreenTrivia Green { get; }

    /// <summary>The trivia's text.</summary>
    public override string ToString() => Text;
}
