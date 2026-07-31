namespace Norristown.Syntax.Text
{
    /// <summary>
    /// A zero-based position within a <see cref="SourceText"/>; a (line, column) pair.
    /// </summary>
    public readonly record struct LinePosition
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LinePosition"/> type.
        /// </summary>
        /// <param name="line">The zero-based line index.</param>
        /// <param name="column">The zero-based column index.</param>
        public LinePosition(int line, int column)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(line);
            ArgumentOutOfRangeException.ThrowIfNegative(column);

            this.Line = line;
            this.Column = column;
        }

        /// <summary>
        /// Gets the zero-based line index of this position.
        /// </summary>
        public int Line { get; }

        /// <summary>
        /// Gets the zero-based column index of this position.
        /// </summary>
        public int Column { get; }

        /// <inheritdoc/>
        public override String ToString() => $"({Line},{Column})";
    }
}
