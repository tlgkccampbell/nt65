using System.Text;

namespace Norristown.Syntax.Green
{
    /// <summary>
    /// A leaf in the green tree, consisting of a token and its leading trivia.
    /// </summary>
    internal sealed class GreenToken : GreenNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GreenToken"/> type.
        /// </summary>
        /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of token this is.</param>
        /// <param name="trivia">The token's leading trivia.</param>
        /// <param name="text">The token's text.</param>
        /// <param name="attributes">The token's attributes.</param>
        internal GreenToken(SyntaxKind kind, GreenTrivia[]? trivia, String text, GreenTokenAttributes attributes)
            : base(kind, GreenTriviaExtensions.SumWidths(trivia) + text.Length)
        {
            this.Trivia = trivia ?? [];
            this.Text = text;
            this.Attributes = attributes;
        }

        /// <inheritdoc/>
        public override GreenNode? GetSlot(Int32 index) => null;

        /// <summary>
        /// Gets the token's text as it appears in the source.
        /// </summary>
        public String Text { get; }

        /// <summary>
        /// Gets the width of the token, excluding leading trivia.
        /// </summary>
        public Int32 Width => Text.Length;

        /// <summary>
        /// Gets the width of the token's leading trivia.
        /// </summary>
        public Int32 TriviaWidth => FullWidth - Text.Length;

        /// <summary>
        /// Gets the set of <see cref="GreenTokenAttributes"/> attached to this token.
        /// </summary>
        public GreenTokenAttributes Attributes { get; }

        /// <summary>
        /// Gets a value indicating whether this is a missing token.
        /// </summary>
        public Boolean IsMissing => 
            (Attributes & GreenTokenAttributes.IsMissing) == GreenTokenAttributes.IsMissing;

        /// <summary>
        /// Gets a value indicating whether this token is preceded by whitespace.
        /// </summary>
        public Boolean IsPrecededByWhitespace => 
            (Attributes & GreenTokenAttributes.IsPrecededByWhitespace) == GreenTokenAttributes.IsPrecededByWhitespace;

        /// <inheritdoc/>
        public override Boolean IsToken => true;

        /// <summary>
        /// Gets the token's leading trivia.
        /// </summary>
        public GreenTrivia[] Trivia { get; }

        /// <inheritdoc/>
        protected override void WriteTo(StringBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            foreach (var trivia in Trivia)
            {
                builder.Append(trivia.Text);
            }

            builder.Append(Text);
        }
    }
}
