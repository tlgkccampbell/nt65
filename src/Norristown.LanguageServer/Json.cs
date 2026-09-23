using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Just enough of a JSON scanner to find one string in <c>nt65.json</c> and put another in its
/// place. nt65 reads the file with comments and trailing commas allowed, and a reader that
/// parses it gives back values and not the places they were written; an edit needs the place.
/// </summary>
internal static class Json
{
    /// <summary>
    /// Where the string <paramref name="value"/> is written in the array under
    /// <paramref name="key"/>, quotes and all, or null when it is not written there. A comment
    /// is passed over, so text inside one is never mistaken for an entry.
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

    /// <summary>A string as JSON writes it, with the two characters that have to be escaped escaped.</summary>
    public static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Just past the key's own string, or -1 when the file does not write it at the top level.</summary>
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

    /// <summary>Just past a string that starts at <paramref name="at"/>, its closing quote included.</summary>
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

    /// <summary>Past one character, or past the whole of a comment where one starts here.</summary>
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

    /// <summary>The value of a JSON string's contents, undoing the escapes a path may contain.</summary>
    private static string Read(string written) =>
        written.Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
}
