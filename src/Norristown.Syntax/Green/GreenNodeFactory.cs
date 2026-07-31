namespace Norristown.Syntax.Green
{
    /// <summary>
    /// Contains factory methods for constructing green nodes.
    /// </summary>
    internal static class GreenNodeFactory
    {
        /// <summary>
        /// Creates a token.
        /// </summary>
        /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of token this is.</param>
        /// <param name="trivia">The token's leading trivia.</param>
        /// <param name="text">The token's text.</param>
        /// <param name="attributes">The token's attributes.</param>
        /// <returns>A <see cref="GreenNode"/> that represents the created token.</returns>
        public static GreenNode Token(SyntaxKind kind, GreenTrivia[]? trivia, String text, GreenTokenAttributes attributes) =>
            new GreenToken(kind, trivia, text, attributes);

        /// <summary>
        /// Creates a node.
        /// </summary>
        /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of syntax node this is.</param>
        /// <param name="slots">The node's collection of child nodes, with <see langword="null"/> representing omitted children.</param>
        /// <returns>A <see cref="GreenNode"/> that represents the created node.</returns>
        public static GreenNode Node(SyntaxKind kind, GreenNode?[] slots) =>
            new GreenSyntaxNode(kind, slots);

        /// <summary>
        /// Creates trivia.
        /// </summary>
        /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of trivia this is.</param>
        /// <param name="text">The trivia's text as it appears in the source.</param>
        /// <returns>A <see cref="GreenTrivia"/> that represents the created trivia.</returns>
        public static GreenTrivia Trivia(SyntaxKind kind, String text) =>
            new(kind, text);
    }
}
