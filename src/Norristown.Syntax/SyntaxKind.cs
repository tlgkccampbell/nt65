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
    }
}
