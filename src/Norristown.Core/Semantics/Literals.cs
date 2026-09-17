using System.Globalization;
using System.Text;

namespace Norristown.Semantics;

/// <summary>
/// What a literal token means. The lexer has already said whether a literal is
/// well formed, so these read a token the lexer accepted and give up quietly on one it
/// did not.
/// </summary>
public static class Literals
{
    /// <summary>
    /// The value of a number token: <c>$1F</c>, <c>%1010</c> or <c>255</c>, with a <c>_</c>
    /// between digits counting for nothing.
    /// </summary>
    public static long? Number(string text) => text.Length switch
    {
        0 => null,
        _ => text[0] switch
        {
            '$' => Parse(text[1..], 16),
            '%' => Parse(text[1..], 2),
            _ => Parse(text, 10),
        },
    };

    /// <summary>
    /// The text of a string or character literal, with its escapes applied. Outside a
    /// charmap the text is ASCII, and <c>\xHH</c> writes any byte; a byte above <c>$7f</c>
    /// is kept as the character of that code, which is what a charmap will map.
    /// </summary>
    public static string? Text(string literal)
    {
        if (literal.Length < 2 || literal[0] != literal[^1])
            return null;

        var body = literal.AsSpan(1, literal.Length - 2);
        var text = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\')
            {
                text.Append(body[i]);
                continue;
            }
            if (++i >= body.Length)
                return null;
            switch (body[i])
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case '0': text.Append('\0'); break;
                case '\\': text.Append('\\'); break;
                case '"': text.Append('"'); break;
                case '\'': text.Append('\''); break;
                case 'x' when i + 2 < body.Length:
                    text.Append((char)int.Parse(body.Slice(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    return null;
            }
        }
        return text.ToString();
    }

    /// <summary>The code of a character literal, <c>'c'</c>.</summary>
    public static long? Character(string literal) =>
        Text(literal) is { Length: 1 } text ? text[0] : null;

    private static long? Parse(string digits, int radix)
    {
        if (digits.Length == 0)
            return null;
        long value = 0;
        foreach (var c in digits)
        {
            // `_` between digits is a separator and counts for nothing; the lexer has already
            // said whether it stands where one may.
            if (c == '_')
                continue;
            var digit = char.IsAsciiDigit(c) ? c - '0'
                : char.IsAsciiHexDigit(c) ? char.ToLowerInvariant(c) - 'a' + 10
                : -1;
            if (digit < 0 || digit >= radix)
                return null;

            // A literal too large for the value type says nothing useful; the stage that
            // writes it out is where a range matters.
            try
            {
                value = checked(value * radix + digit);
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return value;
    }
}
