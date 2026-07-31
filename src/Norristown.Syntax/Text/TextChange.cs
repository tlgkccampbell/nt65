namespace Norristown.Syntax.Text
{
    /// <summary>
    /// A replacement of one range of a <see cref="SourceText"/> with new text.
    /// </summary>
    public readonly record struct TextChange
    {
        private readonly TextSpan _span;
        private readonly String? _newText;

        /// <summary>
        /// Initializes a new instance of the <see cref="TextChange"/> type.
        /// </summary>
        /// <param name="span">The range to replace, in the coordinates of the text being changed.</param>
        /// <param name="newText">The text to put in its place, possibly empty.</param>
        public TextChange(TextSpan span, String newText)
        {
            ArgumentNullException.ThrowIfNull(newText);

            _span = span;
            _newText = String.IsNullOrEmpty(newText) ? null : newText;
        }

        /// <summary>
        /// Gets the range being replaced, in the coordinates of the text being changed.
        /// </summary>
        public TextSpan Span => _span;

        /// <summary>
        /// Gets the replacement text, which is empty for a deletion.
        /// </summary>
        public String NewText => _newText ?? String.Empty;

        /// <inheritdoc/>
        public override String ToString() => $"{Span} -> \"{NewText}\"";
    }
}
