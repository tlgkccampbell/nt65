using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>
/// The integer arithmetic the compile-time built-ins are worked out in. Every answer is a whole
/// number and nothing here uses floating point, so the same program gives the same bytes on
/// every machine. What each function promises is therefore checked exactly.
/// </summary>
public sealed class IntegerMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(255, 15)]
    [InlineData(256, 16)]
    [InlineData(4095, 63)]
    [InlineData(4096, 64)]
    [InlineData(long.MaxValue, 3037000499)]
    public void SqrtIsTheLargestWholeNumberWhoseSquareFits(long n, long root) =>
        Assert.Equal(root, IntegerMath.Sqrt(n));

    /// <summary>A square is never negative, so a negative number has no root here.</summary>
    [Fact]
    public void SqrtOfANegativeHasNoAnswer() => Assert.Null(IntegerMath.Sqrt(-1));

    [Theory]
    // The product is worked out exactly, so nothing overflows in the middle.
    [InlineData(1L << 62, 4, 8, 1L << 61)]
    [InlineData(1000000, 1000000, 1000000, 1000000)]

    // A result exactly halfway between two whole numbers rounds away from zero, in either sign.
    [InlineData(1, 1, 2, 1)]
    [InlineData(3, 1, 2, 2)]
    [InlineData(-1, 1, 2, -1)]
    [InlineData(-3, 1, 2, -2)]
    [InlineData(1, 1, 3, 0)]
    [InlineData(2, 1, 3, 1)]

    // A negative divisor rounds the same way, with the sign it gives the result.
    [InlineData(10, 1, -3, -3)]
    [InlineData(-10, 1, -3, 3)]
    [InlineData(3, 1, -2, -2)]
    [InlineData(-3, 1, -2, 2)]
    public void MulDivIsTheExactProductRoundedToTheNearest(long a, long b, long c, long expected) =>
        Assert.Equal(expected, IntegerMath.MulDiv(a, b, c));

    [Fact]
    public void MulDivByZeroHasNoAnswer() => Assert.Null(IntegerMath.MulDiv(1, 1, 0));

    /// <summary>A result that does not fit in 64 bits is no answer at all, rather than a wrapped one.</summary>
    [Fact]
    public void MulDivThatLeavesSixtyFourBitsHasNoAnswer() =>
        Assert.Null(IntegerMath.MulDiv(long.MaxValue, 4, 2));

    [Theory]
    // The quarter turns, which are exact whatever the scale.
    [InlineData(0, 256, 127, 0)]
    [InlineData(64, 256, 127, 127)]
    [InlineData(128, 256, 127, 0)]
    [InlineData(192, 256, 127, -127)]
    [InlineData(256, 256, 127, 0)]

    // The angle goes round the circle, so a table written with a running index needs no
    // wrapping of its own, and a negative one is the same angle the other way.
    [InlineData(320, 256, 127, 127)]
    [InlineData(-64, 256, 127, -127)]

    // A twelfth of a turn is where the sine is exactly a half. That angle, with its mirror
    // images around the circle, is the one place where the answer can sit halfway between two
    // whole numbers, and the rule sends it away from zero.
    [InlineData(1, 12, 127, 64)]
    [InlineData(5, 12, 127, 64)]
    [InlineData(7, 12, 127, -64)]
    [InlineData(11, 12, 127, -64)]
    [InlineData(1, 12, 126, 63)]
    [InlineData(7, 12, 126, -63)]

    // The rest of the circle, at the scale a byte table is written in.
    [InlineData(32, 256, 127, 90)]
    [InlineData(16, 256, 127, 49)]
    [InlineData(1, 256, 127, 3)]
    [InlineData(1, 360, 10000, 175)]
    [InlineData(30, 360, 10000, 5000)]
    [InlineData(45, 360, 10000, 7071)]
    public void SinIsTheNearestWholeNumberToTheScaledSine(long angle, long turn, long scale, long expected) =>
        Assert.Equal(expected, IntegerMath.Sin(angle, turn, scale));

    /// <summary>The cosine is the sine a quarter turn on, and is exact where the sine is.</summary>
    [Theory]
    [InlineData(0, 256, 127, 127)]
    [InlineData(64, 256, 127, 0)]
    [InlineData(128, 256, 127, -127)]
    [InlineData(192, 256, 127, 0)]
    [InlineData(2, 12, 127, 64)]
    [InlineData(60, 360, 10000, 5000)]
    [InlineData(45, 360, 10000, 7071)]
    public void CosIsTheSineAQuarterTurnOn(long angle, long turn, long scale, long expected) =>
        Assert.Equal(expected, IntegerMath.Cos(angle, turn, scale));

    /// <summary>
    /// Every table rests on one identity. At the scale a table is written in, the squares of the
    /// sine and the cosine of one angle add up to the square of the scale, give or take the
    /// rounding each of them took.
    /// </summary>
    [Fact]
    public void SineAndCosineSquareToTheScale()
    {
        for (var angle = 0; angle < 256; angle++)
        {
            var sine = IntegerMath.Sin(angle, 256, 1000)!.Value;
            var cosine = IntegerMath.Cos(angle, 256, 1000)!.Value;
            var radius = IntegerMath.Sqrt((sine * sine) + (cosine * cosine))!.Value;
            Assert.InRange(radius, 999, 1000);
        }
    }

    /// <summary>A turn or a scale outside what the functions take has no answer.</summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(-256, 100)]
    [InlineData(256, 0x80000000)]
    [InlineData(0x80000000, 100)]
    public void ATurnOrScaleOutOfRangeHasNoAnswer(long turn, long scale)
    {
        Assert.False(IntegerMath.InRange(turn, scale));
        Assert.Null(IntegerMath.Sin(1, turn, scale));
        Assert.Null(IntegerMath.Cos(1, turn, scale));
    }
}
