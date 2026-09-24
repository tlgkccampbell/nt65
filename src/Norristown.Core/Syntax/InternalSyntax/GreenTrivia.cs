namespace Norristown.Syntax.InternalSyntax;

/// <summary>Represents whitespace or a comment, held by the token it sits next to.</summary>
/// <param name="kind">The trivia's kind, which is whitespace or comment.</param>
/// <param name="text">The trivia's text, exactly as in the source.</param>
internal sealed class GreenTrivia(SyntaxKind kind, string text)
{
    /// <summary>Gets the trivia's kind, which says whether it is whitespace or a comment.</summary>
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Gets the trivia's text, exactly as in the source.</summary>
    public string Text { get; } = text;
}
