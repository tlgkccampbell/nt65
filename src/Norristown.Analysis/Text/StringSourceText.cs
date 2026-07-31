using System.Text;

namespace Norristown.Analysis.Text
{
    /// <summary>
    /// A basic implementation of <see cref="SourceText"/> representing the entire text
    /// as a single string.
    /// </summary>
    internal sealed class StringSourceText : SourceText
    {
        private readonly String _text;

        /// <summary>
        /// Initializes a new instance of the <see cref="StringSourceText"/> class.
        /// </summary>
        /// <param name="text">The source text.</param>
        /// <param name="revision">The source text's revision number.</param>
        internal StringSourceText(String text, Int64 revision = 0)
            : base(revision)
        {
            ArgumentNullException.ThrowIfNull(text);

            _text = text;
        }

        /// <inheritdoc/>
        public override Int32 Length => _text.Length;

        /// <inheritdoc/>
        public override String ToString() => _text;

        /// <inheritdoc/>
        protected override void CopyToCore(TextSpan span, Span<Char> destination) => 
            _text.AsSpan(span.Start, span.Length).CopyTo(destination);

        /// <inheritdoc/>
        protected override Char GetCharCore(Int32 position) => 
            _text[position];

        /// <inheritdoc/>
        protected override SourceText ApplyChanges(ReadOnlySpan<TextChange> changes)
        {
            var builder = new StringBuilder(_text.Length);

            var position = 0;
            foreach (var change in changes)
            {
                builder.Append(_text, position, change.Span.Start - position);
                builder.Append(change.NewText);

                position = change.Span.End;
            }

            builder.Append(_text, position, _text.Length - position);

            return new StringSourceText(builder.ToString(), Revision + 1);
        }
    }
}
