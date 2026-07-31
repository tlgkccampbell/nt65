using System;
using System.Collections.Generic;
using System.Text;

namespace Norristown.Syntax.Green
{
    /// <summary>
    /// An immutable, position-independent node in a green syntax tree.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="GreenNode"/> type.
    /// </remarks>
    /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of node this is.</param>
    /// <param name="fullWidth">The full width of the node in characters, including trivia.</param>
    internal abstract class GreenNode(SyntaxKind kind, Int32 fullWidth)
    {
        /// <summary>
        /// Gets the child in the specified slot.
        /// </summary>
        /// <param name="index">The slot index.</param>
        /// <returns>The requested child, or <see langword="null"/> if the slot contains an omitted optional child
        /// or the slot index of out of range.</returns>
        public virtual GreenNode? GetSlot(Int32 index) => null;

        /// <summary>
        /// Gets a <see cref="SyntaxKind"/> value that describes what kind of node this is.
        /// </summary>
        public SyntaxKind Kind { get; } = kind;

        /// <summary>
        /// Gets the full width of the node in characters, including trivia.
        /// </summary>
        public Int32 FullWidth { get; } = fullWidth;

        /// <summary>
        /// Gets the number of child slots this node has.
        /// </summary>
        public virtual Int32 SlotCount => 0;

        /// <summary>
        /// Gets a value indicating whether this node is a leaf/token.
        /// </summary>
        public virtual Boolean IsToken => false;

        /// <summary>
        /// Gets the full text of this node, including trivia.
        /// </summary>
        /// <returns>The full text of this node.</returns>
        public string ToFullString()
        {
            var builder = new StringBuilder(FullWidth);
            WriteTo(builder);
            return builder.ToString();
        }

        /// <summary>
        /// Appends the full text of this node, including trivia, to the specified builder.
        /// </summary>
        /// <param name="builder">The builder to which the node's text will be appended.</param>
        protected virtual void WriteTo(StringBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            for (var index = 0; index < SlotCount; index++)
            {
                GetSlot(index)?.WriteTo(builder);
            }
        }
    }
}
