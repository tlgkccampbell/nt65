using System.Globalization;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Applies operators to numbers. Arithmetic is 64-bit and signed, and an operation whose result
/// does not fit in 64 bits has no value. Nothing wraps, because a wrapped result is a number
/// nobody wrote.
/// <para>
/// Nothing is reported from here. Where there is no value, <c>refused</c> gives the reason, and
/// the caller reports it at the span it has. A division by zero sets no reason, because the
/// caller knows which operand is zero and can report it more precisely.
/// </para>
/// </summary>
internal static class Operators
{
    /// <summary>
    /// Returns the result of applying the prefix operator <paramref name="op"/> to
    /// <paramref name="value"/>.
    /// </summary>
    public static long? Unary(SyntaxKind op, long value, out DiagnosticMessage? refused)
    {
        // Negating the smallest number is the one prefix operation with no result, because its
        // positive is one past the largest number.
        if (op == SyntaxKind.Minus && value == long.MinValue)
        {
            refused = Overflows($"negating {Value.Of(value)}");
            return null;
        }
        refused = null;
        return op switch
        {
            SyntaxKind.Plus => value,
            SyntaxKind.Minus => -value,
            SyntaxKind.Tilde => ~value,
            SyntaxKind.Bang => value == 0 ? 1 : 0,
            SyntaxKind.Less => value & 0xff,
            SyntaxKind.Greater => (value >> 8) & 0xff,
            SyntaxKind.Caret => (value >> 16) & 0xff,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the result of applying the binary operator <paramref name="op"/> to
    /// <paramref name="a"/> and <paramref name="b"/>. <c>.mod</c> arrives as a directive token,
    /// because <c>%</c> begins a binary number.
    /// </summary>
    public static long? Binary(SyntaxToken op, long a, long b, out DiagnosticMessage? refused)
    {
        refused = null;
        switch (op.Kind)
        {
            // The four operators that can overflow 64 bits are computed in 128 bits, and give a
            // value only when the result fits in 64 bits after all.
            case SyntaxKind.Star:
                return Within((Int128)a * b, op, a, b, out refused);
            case SyntaxKind.Plus:
                return Within((Int128)a + b, op, a, b, out refused);
            case SyntaxKind.Minus:
                return Within((Int128)a - b, op, a, b, out refused);
            case SyntaxKind.LessLess:
                return Counted(b, out refused) ? Within((Int128)a << (int)b, op, a, b, out refused) : null;

            // A shift right keeps the sign, and can lose nothing but the bits it shifts out.
            case SyntaxKind.GreaterGreater:
                return Counted(b, out refused) ? a >> (int)b : null;

            // Division truncates toward zero, so the quotient of the smallest number and -1 is one
            // past the largest.
            case SyntaxKind.Slash:
                return b == 0 ? null : Within((Int128)a / b, op, a, b, out refused);

            // `.mod` takes the dividend's sign. A remainder by -1 is zero for any dividend. That
            // case is handled explicitly because the machine instruction traps on it.
            case SyntaxKind.Directive:
                return b == 0 ? null : b == -1 ? 0 : a % b;
            default:
                return Plain(op.Kind, a, b);
        }
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="op"/> divides, so that a zero on its
    /// right is an error.
    /// </summary>
    public static bool Divides(SyntaxToken op) =>
        op.Kind == SyntaxKind.Slash
        || (op.Kind == SyntaxKind.Directive && op.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a value indicating whether <paramref name="op"/> decides its result from
    /// <paramref name="left"/> alone, so that the right operand is neither evaluated nor looked
    /// up.
    /// </summary>
    public static bool ShortCircuits(SyntaxKind op, long left) => op switch
    {
        SyntaxKind.AmpersandAmpersand => left == 0,
        SyntaxKind.BarBar => left != 0,
        _ => false,
    };

    /// <summary>
    /// Applies an operator that cannot overflow 64 bits, which are the comparison, bitwise and
    /// logical operators.
    /// </summary>
    private static long? Plain(SyntaxKind op, long a, long b) => op switch
    {
        SyntaxKind.Less => Truth(a < b),
        SyntaxKind.LessEquals => Truth(a <= b),
        SyntaxKind.Greater => Truth(a > b),
        SyntaxKind.GreaterEquals => Truth(a >= b),
        SyntaxKind.EqualsEquals => Truth(a == b),
        SyntaxKind.BangEquals => Truth(a != b),
        SyntaxKind.Ampersand => a & b,
        SyntaxKind.Caret => a ^ b,
        SyntaxKind.Bar => a | b,
        SyntaxKind.AmpersandAmpersand => Truth(a != 0 && b != 0),
        SyntaxKind.CaretCaret => Truth((a != 0) ^ (b != 0)),
        SyntaxKind.BarBar => Truth(a != 0 || b != 0),
        _ => null,
    };

    /// <summary>
    /// Returns <paramref name="result"/> when it fits in a 64-bit number. Otherwise, returns null
    /// and sets <paramref name="refused"/> to the reason.
    /// </summary>
    private static long? Within(Int128 result, SyntaxToken op, long a, long b, out DiagnosticMessage? refused)
    {
        if (result >= long.MinValue && result <= long.MaxValue)
        {
            refused = null;
            return (long)result;
        }
        refused = Overflows(Format(op, a, b));
        return null;
    }

    /// <summary>
    /// Formats an operation the way the overflow message quotes it. A shift count is formatted as
    /// a small decimal number, unlike every other value, which may be a mask or an address.
    /// </summary>
    private static string Format(SyntaxToken op, long a, long b) => op.Kind == SyntaxKind.LessLess
        ? $"{Value.Of(a)} {op.Text} {b.ToString(CultureInfo.InvariantCulture)}"
        : $"{Value.Of(a)} {op.Text} {Value.Of(b)}";

    /// <summary>
    /// Returns a value indicating whether a shift count is from 0 to 63, and sets
    /// <paramref name="refused"/> when it is not.
    /// </summary>
    private static bool Counted(long places, out DiagnosticMessage? refused)
    {
        refused = places is < 0 or > 63
            ? Catalogue.ShiftCountOutOfRange.Message(places.ToString(CultureInfo.InvariantCulture))
            : (DiagnosticMessage?)null;
        return refused is null;
    }

    private static DiagnosticMessage Overflows(string expression) => Catalogue.ArithmeticOverflow.Message(expression);

    private static long Truth(bool condition) => condition ? 1 : 0;
}
