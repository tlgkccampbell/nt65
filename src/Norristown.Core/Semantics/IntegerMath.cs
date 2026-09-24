using System.Globalization;
using System.Numerics;

namespace Norristown.Semantics;

/// <summary>
/// Provides the arithmetic behind the built-in functions that compute a number rather than
/// query the program. These are a square root, a scaled product and the two trigonometric
/// functions used to build tables. Every one of them takes whole numbers and returns a whole
/// number.
/// <para>
/// Nothing here uses floating point, and nothing here may. A declaration's value is emitted into
/// the output, so two machines that disagreed about the last bit of a sine would assemble
/// different bytes from one program. Each result is defined as the whole number nearest to an
/// exact value, with halves rounded away from zero, and is computed in exact integer arithmetic.
/// The exact value is a half only where the sine or the cosine is <c>±1/2</c>. There the result
/// is computed from that fraction, not from a series, so the rounding rule decides it rather
/// than the last bit of an approximation.
/// </para>
/// </summary>
public static class IntegerMath
{
    /// <summary>
    /// The largest turn and scale the trigonometric functions accept. A value in the output must
    /// fit ca65's 32 bits anyway, and bounding both keeps the intermediate numbers comfortably
    /// small.
    /// </summary>
    public const long Limit = 0x7fffffff;

    /// <summary>
    /// The number of fraction bits the series is computed to. This precision far exceeds the
    /// distance between two neighbouring results at any scale the language accepts. The one case
    /// where an exact value could sit halfway between two results is computed exactly instead,
    /// so the nearest whole number never depends on the last bit.
    /// </summary>
    private const int Bits = 192;

    /// <summary>
    /// π/2 as a fraction with <see cref="Bits"/> bits after the point. It is a literal rather than
    /// a computed value, so that it is the same number on every machine and in every release.
    /// </summary>
    private static readonly BigInteger HalfPi = BigInteger.Parse(
        "01921fb54442d18469898cc51701b839a252049c1114cf98e8",
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns a value indicating whether the trigonometric functions accept
    /// <paramref name="turn"/> and <paramref name="scale"/>.
    /// </summary>
    public static bool InRange(long turn, long scale) => turn is > 0 and <= Limit && Math.Abs(scale) <= Limit;

    /// <summary>
    /// Returns the largest whole number whose square is at most <paramref name="n"/>, or null
    /// when <paramref name="n"/> is negative and there is no such number.
    /// </summary>
    public static long? Sqrt(long n)
    {
        if (n < 0)
            return null;
        if (n == 0)
            return 0;

        // The first guess is above the answer and comes from the number's length: half its bits,
        // rounded up. Each step of Newton's method stays above the answer and decreases, so the
        // first step that does not decrease is the answer.
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
    /// Returns <paramref name="a"/> times <paramref name="b"/> divided by <paramref name="c"/>.
    /// The product is computed exactly at any size, and the quotient is rounded to the nearest
    /// whole number, with halves rounded away from zero. Returns null when <paramref name="c"/> is
    /// zero or the result does not fit in 64 bits.
    /// </summary>
    public static long? MulDiv(long a, long b, long c) =>
        c == 0 ? null : Fits(Rounded(new BigInteger(a) * b, c));

    /// <summary>
    /// Returns <paramref name="scale"/> times the sine of <paramref name="angle"/>, where a whole
    /// turn is <paramref name="turn"/> of the angle's units. The result is rounded to the nearest
    /// whole number, with halves rounded away from zero. Returns null when the turn or the scale
    /// is outside the range the function accepts.
    /// </summary>
    public static long? Sin(long angle, long turn, long scale) => Circle(angle, turn, scale, cosine: false);

    /// <summary>
    /// Returns <paramref name="scale"/> times the cosine of <paramref name="angle"/>, where a
    /// whole turn is <paramref name="turn"/> of the angle's units. The result is rounded to the
    /// nearest whole number, with halves rounded away from zero. Returns null when the turn or the
    /// scale is outside the range the function accepts.
    /// </summary>
    public static long? Cos(long angle, long turn, long scale) => Circle(angle, turn, scale, cosine: true);

    /// <summary>
    /// Returns the sine or the cosine, scaled and rounded. The angle is first reduced to one turn,
    /// so a table built with a running index needs no wrapping of its own. A cosine is computed
    /// as the sine a quarter turn further on.
    /// </summary>
    private static long? Circle(long angle, long turn, long scale, bool cosine)
    {
        if (!InRange(turn, scale))
            return null;

        // The angle is expressed as a fraction with four times the turn as its denominator, so
        // that the quarter turn a cosine adds is a whole number of the same units for any turn.
        var denominator = 4 * turn;
        var numerator = (((4 * (angle % turn)) + (cosine ? turn : 0)) + denominator) % denominator;

        // Where the angle is a multiple of a twelfth of a turn, the sine is computed from its
        // exact fraction. Those are the only angles at which the exact value can sit halfway
        // between two results, and a series would leave the rounding to its own last bit rather
        // than to the rule.
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
    /// Returns the sine of <c>numerator/denominator</c> of a turn, times <paramref name="scale"/>
    /// and rounded. The quarter the angle lands in decides which series is computed and with which
    /// sign. This keeps every series within the first quarter turn, where it converges fastest.
    /// </summary>
    private static long? Scaled(long numerator, long denominator, long scale)
    {
        var quarter = (int)(4 * numerator / denominator);
        var into = (4 * numerator) % denominator;
        var value = Series(into, denominator, sine: (quarter & 1) == 0);
        return Fits(Rounded((quarter >= 2 ? -value : value) * scale, BigInteger.One << Bits));
    }

    /// <summary>
    /// Returns the sine or the cosine of <c>(π/2)(into/turn)</c>, as a fraction with
    /// <see cref="Bits"/> bits after the point. The angle is at most a quarter turn, so the series
    /// converges in a few dozen terms. Each term is computed from the one before it.
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
    /// Returns <paramref name="value"/> divided by <paramref name="by"/>, rounded to the nearest
    /// whole number, with halves rounded away from zero. <paramref name="by"/> must be positive.
    /// </summary>
    private static BigInteger Rounded(BigInteger value, BigInteger by)
    {
        var negative = value.Sign < 0;
        var magnitude = negative ? -value : value;
        var rounded = ((magnitude * 2) + by) / (by * 2);
        return negative ? -rounded : rounded;
    }

    /// <summary>
    /// Returns <paramref name="value"/> as a 64-bit number, or null if it does not fit in one.
    /// </summary>
    private static long? Fits(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue ? (long)value : null;
}
