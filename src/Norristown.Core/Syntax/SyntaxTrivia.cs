using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents whitespace or a comment, together with the token it belongs to and its position in
/// the file.
/// </summary>
public readonly record struct SyntaxTrivia
{
    internal SyntaxTrivia(SyntaxToken token, GreenTrivia green, int position)
    {
        Token = token;
        Green = green;
        Position = position;
    }

    /// <summary>Gets the token that this trivia belongs to.</summary>
    public SyntaxToken Token { get; }

    /// <summary>Gets the offset in the file's text where the trivia starts.</summary>
    public int Position { get; }

    /// <summary>Gets the kind of this trivia, which is whitespace or a comment.</summary>
    public SyntaxKind Kind => Green.Kind;

    /// <summary>Gets the trivia's text, exactly as in the source.</summary>
    public string Text => Green.Text;

    /// <summary>Gets the trivia's range in the file's text.</summary>
    public TextSpan Span => new(Position, Green.Text.Length);

    /// <summary>
    /// Gets the trivia's full range, which is the same as <see cref="Span"/> because trivia has
    /// no trivia of its own around it.
    /// </summary>
    public TextSpan FullSpan => Span;

    /// <summary>Gets the green trivia that this trivia wraps.</summary>
    internal GreenTrivia Green { get; }

    /// <summary>Returns the trivia's text.</summary>
    public override string ToString() => Text;
}
