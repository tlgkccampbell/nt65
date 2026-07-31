namespace Norristown.Syntax.Green
{
    /// <summary>
    /// A set of flags that can be attached to tokens to indicate various important attributes.
    /// </summary>
    [Flags]
    internal enum GreenTokenAttributes : byte
    {
        /// <summary>
        /// No special attributes.
        /// </summary>
        None = 0,

        /// <summary>
        /// The token is missing. Used during error recovery.
        /// </summary>
        IsMissing = 0x01,

        /// <summary>
        /// The token is preceded by whitespace.
        /// </summary>
        IsPrecededByWhitespace = 0x02,
    }
}
