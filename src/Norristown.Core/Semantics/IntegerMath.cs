using System.Globalization;
using System.Numerics;

namespace Norristown.Semantics;

/// <summary>
/// The arithmetic behind the built-in functions that work a number out rather than ask about
/// the program: a square root, a scaled product and the two trigonometric functions a table is
/// built with. Every one of them takes whole numbers and answers a whole number.
/// <para>
/// Nothing here is floating point, and nothing here may be: a declaration's value is
/// written into the output, so two machines that disagreed about the last bit of a sine would
/// assemble different bytes from one program. Each answer is defined as a whole number — the
/// nearest one to an exact value, with a half going away from zero — and is worked out in exact
/// integer arithmetic. Where the exact value is a half, which is only where the sine or the
/// cosine is <c>±1/2</c>, the answer is worked out from that fraction rather than from a series,
/// so the rule decides it rather than the last bit of an approximation.
/// </para>
/// </summary>
public static class IntegerMath
{
    /// <summary>
    /// How many bits of the fraction the series is worked out to. It is far more than the
    /// distance between two neighbouring answers at any scale the language takes, and the one
    /// place where an exact value could sit halfway between two answers is worked out exactly
    /// instead, so which whole number is nearest is never a question about the last bit.
    /// </summary>
    private const int Bits = 192;

    /// <summary>
    /// The largest turn and scale the trigonometric functions take. A value the output carries
    /// fits ca65's 32 bits anyway, and bounding both keeps the intermediate numbers comfortably
    /// small.
    /// </summary>
    public const long Limit = 0x7fffffff;

    /// <summary>
    /// π/2 as a fraction with <see cref="Bits"/> bits after the point, written out rather than
    /// worked out, so that it is the same number on every machine and in every release.
    /// </summary>
    private static readonly BigInteger HalfPi = BigInteger.Parse(
        "01921fb54442d18469898cc51701b839a252049c1114cf98e8",
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture);

    /// <summary>Whether a turn and a scale are ones the trigonometric functions take.</summary>
    public static bool InRange(long turn, long scale) => turn is > 0 and <= Limit && Math.Abs(scale) <= Limit;

    /// <summary>
    /// The largest whole number whose square is at most <paramref name="n"/>, or null when
    /// <paramref name="n"/> is negative and there is no such number.
    /// </summary>
    public static long? Sqrt(long n)
    {
        if (n < 0)
            return null;
        if (n == 0)
            return 0;

        // A first guess above the answer, from the number's length: half the bits, rounded up.
        // Each step of Newton's method stays above it and comes down, so the first step that
        // does not come down is the answer.
        var root = 1L << (((64 - BitOperations.LeadingZeroCount((ulong)n)) + 1) / 2);
        while (true)
        {
            var next = (root + (n / root)) / 2;
            if (next >= root)
                return root;
            root = next;
        }
    }

    /// <summary>
    /// <paramref name="a"/> times <paramref name="b"/> divided by <paramref name="c"/>, with the
    /// product worked out exactly however large it is and the quotient rounded to the nearest
    /// whole number, a half going away from zero. Null when <paramref name="c"/> is zero or the
    /// answer leaves 64 bits.
    /// </summary>
    public static long? MulDiv(long a, long b, long c) =>
        c == 0 ? null : Fits(Rounded(new BigInteger(a) * b, c));

    /// <summary>
    /// <paramref name="scale"/> times the sine of <paramref name="angle"/>, where a whole turn
    /// is <paramref name="turn"/> of the angle's units, rounded to the nearest whole number with
    /// a half going away from zero. Null when the turn or the scale is outside what the function
    /// takes.
    /// </summary>
    public static long? Sin(long angle, long turn, long scale) => Circle(angle, turn, scale, cosine: false);

    /// <summary>The same for the cosine.</summary>
    public static long? Cos(long angle, long turn, long scale) => Circle(angle, turn, scale, cosine: true);

    /// <summary>
    /// The sine or the cosine, scaled and rounded. The angle is taken round the circle first, so
    /// a table written with a running index needs no wrapping of its own; a cosine is the sine a
    /// quarter turn further on, which is the same walk with one quarter added.
    /// </summary>
    private static long? Circle(long angle, long turn, long scale, bool cosine)
    {
        if (!InRange(turn, scale))
            return null;

        // The angle as a fraction of the turn, over four times the turn so that the quarter a
        // cosine adds is a whole number of the same units whatever the turn is.
        var denominator = 4 * turn;
        var numerator = (((4 * (angle % turn)) + (cosine ? turn : 0)) + denominator) % denominator;

        // Where the sine is a whole fraction, it is worked out from that fraction: those are
        // the only angles at which the exact value can sit halfway between two answers, and a
        // series would leave the rounding to its own last bit rather than to the rule.
        if (12 * numerator % denominator == 0)
        {
            return (12 * numerator / denominator) switch
            {
                0 or 6 => 0,
                3 => scale,
                9 => -scale,
                1 or 5 => Fits(Rounded(scale, 2)),
                7 or 11 => Fits(Rounded(-scale, 2)),
                _ => Scaled(numerator, denominator, scale),
            };
        }
        return Scaled(numerator, denominator, scale);
    }

    /// <summary>
    /// The sine of <c>numerator/denominator</c> of a turn, times <paramref name="scale"/> and
    /// rounded. The quarter the angle lands in decides which series is worked out and with which
    /// sign, which keeps every series to the first quarter turn, where it settles quickest.
    /// </summary>
    private static long? Scaled(long numerator, long denominator, long scale)
    {
        var quarter = (int)(4 * numerator / denominator);
        var into = (4 * numerator) % denominator;
        var value = Series(into, denominator, sine: (quarter & 1) == 0);
        return Fits(Rounded((quarter >= 2 ? -value : value) * scale, BigInteger.One << Bits));
    }

    /// <summary>
    /// The sine or the cosine of <c>(π/2)(into/turn)</c>, as a fraction with <see cref="Bits"/>
    /// bits after the point. The angle is at most a quarter turn, so the series settles in a few
    /// dozen terms, and each term is worked out from the one before it.
    /// </summary>
    private static BigInteger Series(long into, long turn, bool sine)
    {
        var one = BigInteger.One << Bits;
        var x = HalfPi * into / turn;
        var square = x * x >> Bits;
        var term = sine ? x : one;
        var total = term;
        var subtract = true;

        // The angle is at most a quarter turn, so each term after the first few is a fraction of
        // the one before it and the terms reach zero long before this many. The limit is here
        // so that a mistake in the constants above cannot turn a build into a hang.
        for (var k = sine ? 2 : 1; k < 200; k += 2)
        {
            term = term * square / ((long)k * (k + 1)) >> Bits;
            if (term.IsZero)
                return total;
            total += subtract ? -term : term;
            subtract = !subtract;
        }
        return total;
    }

    /// <summary>
    /// <paramref name="value"/> divided by <paramref name="by"/>, rounded to the nearest whole
    /// number with a half going away from zero. <paramref name="by"/> is positive.
    /// </summary>
    private static BigInteger Rounded(BigInteger value, BigInteger by)
    {
        var negative = value.Sign < 0;
        var magnitude = negative ? -value : value;
        var rounded = ((magnitude * 2) + by) / (by * 2);
        return negative ? -rounded : rounded;
    }

    /// <summary>The value as a 64-bit number, or null where it does not fit one.</summary>
    private static long? Fits(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue ? (long)value : null;
}
