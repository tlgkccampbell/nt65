using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What each operator does to numbers. Two evaluators read expressions — one for the
/// program, one for the conditions that decide which of the program exists — and this is
/// where they agree about what the operators mean.
/// <para>
/// Nothing is reported from here. An operand that is not a number, and a division by zero,
/// yield no value; whoever asked knows enough about the expression to say what is wrong
/// with it.
/// </para>
/// </summary>
internal static class Operators
{
    /// <summary>A prefix operator applied to a number.</summary>
    public static long? Unary(SyntaxKind op, long value) => op switch
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

    /// <summary>
    /// A binary operator applied to two numbers. <c>.mod</c> arrives as a directive token,
    /// because <c>%</c> begins a binary number.
    /// </summary>
    public static long? Binary(SyntaxToken op, long a, long b) => op.Kind switch
    {
        SyntaxKind.Star => a * b,
        SyntaxKind.Slash => HasQuotient(a, b) ? a / b : null,
        SyntaxKind.Directive => HasQuotient(a, b) ? a % b : null,
        SyntaxKind.Plus => a + b,
        SyntaxKind.Minus => a - b,
        SyntaxKind.LessLess => Shift(a, b, left: true),
        SyntaxKind.GreaterGreater => Shift(a, b, left: false),
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

    /// <summary>
    /// Whether a division of <paramref name="a"/> by <paramref name="b"/> has an answer: not by
    /// zero, and not the one pair whose quotient is no number of the type — the least number
    /// over −1, which is one past the greatest. The remainder of that pair is as unanswerable,
    /// and the processor refuses both alike.
    /// </summary>
    private static bool HasQuotient(long a, long b) => b != 0 && !(a == long.MinValue && b == -1);

    /// <summary>A shift by more than the width of a value says nothing, so it has no value.</summary>
    private static long? Shift(long value, long places, bool left) =>
        places is < 0 or > 63 ? null : left ? value << (int)places : value >> (int)places;

    private static long Truth(bool condition) => condition ? 1 : 0;
}
