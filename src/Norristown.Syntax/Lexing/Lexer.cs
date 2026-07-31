using System;
using System.Collections.Generic;
using System.Text;
using Norristown.Syntax.Text;

namespace Norristown.Syntax.Lexing
{
    /// <summary>
    /// A lexer that produces a stream of tokens from a given source text.
    /// </summary>
    internal sealed class Lexer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Lexer"/> type positioned at
        /// the start of the specified source text.
        /// </summary>
        /// <param name="text">The source text being lexed.</param>
        public Lexer(SourceText text)
            : this(text, 0)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Lexer"/> type positioned
        /// partway through the specified source text.
        /// </summary>
        /// <param name="text">The source text being lexed.</param>
        /// <param name="position">The lexer's initial position within the source text.</param>
        public Lexer(SourceText text, Int32 position)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(position, text.Length);

            // TODO
        }
    }
}
