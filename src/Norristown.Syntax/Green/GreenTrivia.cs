namespace Norristown.Syntax.Green
{
    /// <summary>
    /// A piece of trivia (such as whitespace or a comment).
    /// </summary>
    /// <param name="Kind">A <see cref="SyntaxKind"/> value that describes what kind of trivia this is.</param>
    /// <param name="Text">The trivia's text as it appears in the source.</param>
    internal sealed record GreenTrivia(SyntaxKind Kind, String Text)
    {
        /// <summary>
        /// Gets the trivia's width in characters.
        /// </summary>
        public Int32 Width => Text.Length;

        /// <inheritdoc/>
        public override String ToString() => Text;
    }
}
