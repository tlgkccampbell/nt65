namespace Norristown.LanguageServer;

/// <summary>Finds the comment at the end of a line of source, following the lexer's quoting rules.</summary>
internal static class LineComments
{
    /// <summary>
    /// Returns the index of the <c>;</c> that starts the line's comment, or -1 if the line has
    /// none. A <c>;</c> inside a quoted character or string does not start a comment, and
    /// inside quotes a backslash escapes the character after it.
    /// </summary>
    public static int Start(string line)
    {
        var quote = '\0';
        for (var at = 0; at < line.Length; at++)
        {
            var c = line[at];
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
                return at;
            }
        }
        return -1;
    }
}
