using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides just enough of a JSON scanner to find one string in <c>nt65.json</c> and replace it
/// with another. nt65 reads the file with comments and trailing commas allowed. A reader that
/// parses the file returns values, not their positions in the text, and an edit needs the
/// position.
/// </summary>
internal static class Json
{
    /// <summary>
    /// Returns the span of the string <paramref name="value"/> in the array under
    /// <paramref name="key"/>, including its quotes, or null when the array does not contain it.
    /// Comments are skipped, so text inside one is never mistaken for an entry.
    /// </summary>
    public static TextSpan? Entry(string text, string key, string value)
    {
        var at = Start(text, key);
        if (at < 0)
            return null;
        while (at < text.Length && text[at] != '[')
        {
            if (text[at] is '}' or ',')
                return null;
            at = Past(text, at);
        }
        for (at++; at < text.Length && text[at] != ']';)
        {
            if (text[at] != '"')
            {
                at = Past(text, at);
                continue;
            }
            var end = End(text, at);
            if (Read(text[(at + 1)..(end - 1)]) == value)
                return new TextSpan(at, end - at);
            at = end;
        }
        return null;
    }

    /// <summary>
    /// Returns a string as a JSON string literal, with backslashes and double quotes, the two
    /// characters that have to be escaped, escaped.
    /// </summary>
    public static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Returns the position just past the first string in the file equal to the key, or -1 when
    /// there is none. A matching string inside a nested object counts too.
    /// </summary>
    private static int Start(string text, string key)
    {
        var quoted = Quoted(key);
        for (var at = 0; at < text.Length;)
        {
            if (text[at] != '"')
            {
                at = Past(text, at);
                continue;
            }
            var end = End(text, at);
            if (string.CompareOrdinal(text, at, quoted, 0, quoted.Length) == 0 && end == at + quoted.Length)
                return end;
            at = end;
        }
        return -1;
    }

    /// <summary>
    /// Returns the position just past a string that starts at <paramref name="at"/>, including
    /// its closing quote.
    /// </summary>
    private static int End(string text, int at)
    {
        for (var i = at + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == '"')
                return i + 1;
        }
        return text.Length;
    }

    /// <summary>
    /// Returns the position past one character, or past the whole of a comment that starts here.
    /// </summary>
    private static int Past(string text, int at)
    {
        if (at + 1 < text.Length && text[at] == '/' && text[at + 1] == '/')
        {
            var line = text.IndexOf('\n', at);
            return line < 0 ? text.Length : line + 1;
        }
        if (at + 1 < text.Length && text[at] == '/' && text[at + 1] == '*')
        {
            var close = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 2;
        }
        return at + 1;
    }

    /// <summary>
    /// Returns the value of a JSON string's contents, undoing the escapes a path may contain.
    /// </summary>
    private static string Read(string escaped) =>
        escaped.Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
}
