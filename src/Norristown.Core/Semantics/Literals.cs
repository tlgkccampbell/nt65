using System.Globalization;
using System.Text;

namespace Norristown.Semantics;

/// <summary>
/// Converts literal tokens to the values they mean. The lexer has already reported whether a
/// literal is well formed, so these methods read a token the lexer accepted and quietly return
/// null for a token it rejected. An unreadable literal has no value rather than a value nobody
/// meant, and its problem has already been reported where it appears.
/// </summary>
public static class Literals
{
    /// <summary>
    /// Returns the value of a number token such as <c>$1F</c>, <c>%1010</c> or <c>255</c>. A
    /// <c>_</c> between digits is ignored.
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
    /// Returns the text of a string or character literal, with its escapes applied. Outside a
    /// charmap the text is ASCII, and <c>\xHH</c> produces any byte. A byte above <c>$7f</c> is
    /// kept as the character with that code, which a charmap then maps.
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
                case 'x' when i + 2 < body.Length
                    && char.IsAsciiHexDigit(body[i + 1]) && char.IsAsciiHexDigit(body[i + 2]):
                    text.Append((char)int.Parse(body.Slice(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    return null;
            }
        }
        return text.ToString();
    }

    /// <summary>Returns the code of a character literal such as <c>'c'</c>.</summary>
    public static long? Character(string literal) =>
        Text(literal) is { Length: 1 } text ? text[0] : null;

    private static long? Parse(string digits, int radix)
    {
        if (digits.Length == 0)
            return null;
        long value = 0;
        foreach (var c in digits)
        {
            // A `_` between digits is a separator and is ignored. The lexer has already checked
            // that it appears where a separator is allowed.
            if (c == '_')
                continue;
            var digit = char.IsAsciiDigit(c) ? c - '0'
                : char.IsAsciiHexDigit(c) ? char.ToLowerInvariant(c) - 'a' + 10
                : -1;
            if (digit < 0 || digit >= radix)
                return null;

            // A literal too large for a 64-bit value has no useful value, so it gets none. The
            // stage that emits values is where their range is checked.
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
