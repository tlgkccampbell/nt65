namespace Norristown.Syntax
{
    /// <summary>
    /// A value representing every terminal and non-terminal the nt65 grammar can produce.
    /// </summary>
    public enum SyntaxKind : ushort
    {
        /// <summary>
        /// No kind. Never produced by the lexer or parser.
        /// </summary>
        None = 0,

        // ------------------------------------------------------------
        // STRUCTURAL TOKENS                        
        // ------------------------------------------------------------

        /// <summary>
        /// A token that marks the end of a source text.
        /// </summary>
        EndOfFileToken = 1,

        /// <summary>
        /// A token that marks the end of a line. Newlines are not trivia in nt65,
        /// because statements are always terminated by a newline.
        /// </summary>
        NewlineToken,

        // ------------------------------------------------------------
        // TRIVIA
        // ------------------------------------------------------------

        /// <summary>
        /// Horizontal whitespace, such as a space, tab, form feed, etc.
        /// </summary>
        WhitespaceTrivia = 10,

        /// <summary>
        /// A single-line comment.
        /// </summary>
        CommentTrivia,

        /// <summary>
        /// One or more tokens skipped during error recovery.
        /// </summary>
        SkippedTokensTrivia,
    }
}
