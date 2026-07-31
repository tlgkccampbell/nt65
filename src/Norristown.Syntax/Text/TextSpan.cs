namespace Norristown.Syntax.Text
{
    /// <summary>
    /// A contiguous range of characters in a source text.
    /// </summary>
    public readonly record struct TextSpan
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TextSpan"/> type.
        /// </summary>
        /// <param name="start">The zero-based offset of the first character in the span.</param>
        /// <param name="length">The number of characters covered by the span.</param>
        public TextSpan(Int32 start, Int32 length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            this.Start = start;
            this.Length = length;
        }

        /// <summary>
        /// Gets the zero-based offset of the first character in the span.
        /// </summary>
        public Int32 Start { get; }

        /// <summary>
        /// Gets the number of characters the span covers.
        /// </summary>
        public Int32 Length { get; }

        /// <summary>
        /// Gets the offset one past the last character in the span.
        /// </summary>
        public Int32 End => Start + Length;

        /// <summary>
        /// Gets a value indicating whether the span covers no characters.
        /// </summary>
        public Boolean IsEmpty => Length == 0;

        /// <inheritdoc/>
        public override String ToString() => $"[{Start}..{End}]";
    }
}
