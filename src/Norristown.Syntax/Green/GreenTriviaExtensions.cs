namespace Norristown.Syntax.Green
{
    /// <summary>
    /// Contains extension methods for the <see cref="GreenTrivia"/> type.
    /// </summary>
    internal static class GreenTriviaExtensions
    {
        extension(GreenTrivia[]? trivia)
        {
            /// <summary>
            /// Sums the widths of all the trivia in the specified array.
            /// </summary>
            /// <returns>The total width of all the trivia in the array.</returns>
            public Int32 SumWidths()
            {
                var width = 0;
                if (trivia != null)
                {
                    foreach (var piece in trivia)
                    {
                        width += piece.Width;
                    }
                }
                return width;
            }
        }
    }
}
