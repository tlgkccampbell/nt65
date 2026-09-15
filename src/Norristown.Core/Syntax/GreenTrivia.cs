namespace Norristown.Syntax;

/// <summary>Whitespace or a comment, carried by the token it sits next to.</summary>
/// <param name="kind">Whitespace or comment.</param>
/// <param name="text">The trivia's text, exactly as in the source.</param>
public sealed class GreenTrivia(SyntaxKind kind, string text)
{
    /// <summary>Whether this is whitespace or a comment.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>The trivia's text, exactly as in the source.</summary>
    public string Text { get; } = text;
}
