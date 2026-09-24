namespace Norristown.LanguageServer;

/// <summary>
/// Finds the comment at the end of a line of text that has not been lexed, following the lexer's
/// quoting rules. A line of a syntax tree has its comment as trivia already, so this class serves
/// ca65 text and a line cut short at the caret.
/// </summary>
internal static class LineComments
{
    /// <summary>
    /// Returns the index of the <c>;</c> that starts the line's comment, or -1 if the line has
    /// none. A <c>;</c> inside a quoted character or string does not start a comment, and
    /// inside quotes a backslash escapes the character after it.
    /// </summary>
    public static int Start(string line) => Start(line, 0, line.Length, out _);

    /// <summary>
    /// Returns the index of the <c>;</c> that starts a comment in <paramref name="text"/> between
    /// <paramref name="start"/> and <paramref name="end"/>, or -1 if there is none. The quoting
    /// rules are those of <see cref="Start(string)"/>.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The offset where the line starts.</param>
    /// <param name="end">The offset where the scan stops.</param>
    /// <param name="quoted">
    /// Set to whether <paramref name="end"/> falls inside a quoted character or string that is still
    /// open there. It is false when a comment is found.
    /// </param>
    public static int Start(string text, int start, int end, out bool quoted)
    {
        var quote = '\0';
        for (var at = start; at < end; at++)
        {
            var c = text[at];
            if (quote != '\0')
            {
                if (c == '\\')
                    at++;
                else if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ';')
            {
                quoted = false;
                return at;
            }
        }
        quoted = quote != '\0';
        return -1;
    }
}
