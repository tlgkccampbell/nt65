namespace Norristown.Syntax.Green
{
    /// <summary>
    /// Contains extension methods for the <see cref="GreenNode"/> type.
    /// </summary>
    internal static class GreenNodeExtensions
    {
        extension(GreenNode?[] nodes)
        {
            /// <summary>
            /// Sums the full widths of all the nodes in the specified array.
            /// </summary>
            /// <returns>The full width of all the tokens in the array.</returns>
            public Int32 SumFullWidths()
            {
                var width = 0;
                if (nodes != null)
                {
                    foreach (var slot in nodes)
                    {
                        width += slot?.FullWidth ?? 0;
                    }
                }
                return width;
            }
        }
    }
}
