using System.Globalization;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What each operator does to numbers. Arithmetic is 64-bit and signed, and an operation
/// that leaves those 64 bits has no value: nothing wraps, because a wrapped number is one
/// nobody wrote.
/// <para>
/// Nothing is reported from here. Where there is no value, <c>refused</c> says why, for
/// whoever asked to report at the span it knows; a division by zero says nothing, because the
/// caller knows the operand that is zero and says it better.
/// </para>
/// </summary>
internal static class Operators
{
    /// <summary>A prefix operator applied to a number.</summary>
    public static long? Unary(SyntaxKind op, long value, out string? refused)
    {
        // Negating the smallest number there is is the one prefix operation with no answer:
        // its own positive is one past the largest.
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
    /// A binary operator applied to two numbers. <c>.mod</c> arrives as a directive token,
    /// because <c>%</c> begins a binary number.
    /// </summary>
    public static long? Binary(SyntaxToken op, long a, long b, out string? refused)
    {
        refused = null;
        switch (op.Kind)
        {
            // The four that can leave 64 bits are worked out in 128 and answered only where
            // what they came to is a 64-bit number after all.
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

            // Truncating toward zero, so the quotient of the smallest number and -1 is one
            // past the largest.
            case SyntaxKind.Slash:
                return b == 0 ? null : Within((Int128)a / b, op, a, b, out refused);

            // `.mod` takes the dividend's sign. A remainder by -1 is zero whatever the
            // dividend, which is worth saying here because the machine instruction traps on it.
            case SyntaxKind.Directive:
                return b == 0 ? null : b == -1 ? 0 : a % b;
            default:
                return Plain(op.Kind, a, b);
        }
    }

    /// <summary>Whether <paramref name="op"/> divides, so that a zero on its right is an error.</summary>
    public static bool Divides(SyntaxToken op) =>
        op.Kind == SyntaxKind.Slash
        || (op.Kind == SyntaxKind.Directive && op.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="op"/> already decides its result from <paramref name="left"/>
    /// alone, so that the right operand is neither evaluated nor looked up.
    /// </summary>
    public static bool ShortCircuits(SyntaxKind op, long left) => op switch
    {
        SyntaxKind.AmpersandAmpersand => left == 0,
        SyntaxKind.BarBar => left != 0,
        _ => false,
    };

    /// <summary>The operators that cannot leave 64 bits: the comparisons, the bits and the logic.</summary>
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
    /// The result where it is a 64-bit number, and no value with the reason where it is not.
    /// </summary>
    private static long? Within(Int128 result, SyntaxToken op, long a, long b, out string? refused)
    {
        if (result >= long.MinValue && result <= long.MaxValue)
        {
            refused = null;
            return (long)result;
        }
        refused = Overflows(Written(op, a, b));
        return null;
    }

    /// <summary>
    /// An operation as the message spells it back. A shift counts places, which are a small
    /// decimal number rather than the mask or the address every other value is.
    /// </summary>
    private static string Written(SyntaxToken op, long a, long b) => op.Kind == SyntaxKind.LessLess
        ? $"{Value.Of(a)} {op.Text} {b.ToString(CultureInfo.InvariantCulture)}"
        : $"{Value.Of(a)} {op.Text} {Value.Of(b)}";

    /// <summary>Whether a shift counts a number of places a 64-bit value has, and says so if not.</summary>
    private static bool Counted(long places, out string? refused)
    {
        refused = places is < 0 or > 63
            ? $"a shift counts 0 to 63 places, and this one counts {places.ToString(CultureInfo.InvariantCulture)}"
            : null;
        return refused is null;
    }

    private static string Overflows(string written) =>
        $"{written} overflows the 64 bits nt65 computes in";

    private static long Truth(bool condition) => condition ? 1 : 0;
}
