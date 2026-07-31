using Norristown.Syntax.Text;

namespace Norristown.Syntax.Tests.Text
{
    public sealed class TextSpanTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(123, 234)]
        public void Constructor_SetsStartAndLength(Int32 start, Int32 length)
        {
            var span = new TextSpan(start, length);
            Assert.Equal(start, span.Start);
            Assert.Equal(length, span.Length);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(123, 234, 357)]
        public void End_EqualsStartPlusLength(Int32 start, Int32 length, Int32 end)
        {
            var span = new TextSpan(start, length);
            Assert.Equal(end, span.End);
        }

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(123, 0, true)]
        [InlineData(0, 234, false)]
        [InlineData(123, 234, false)]
        public void IsEmpty_IsTrue_WhenLengthIsZero(Int32 start, Int32 length, Boolean expected)
        {
            var span = new TextSpan(start, length);
            Assert.Equal(expected, span.IsEmpty);
            Assert.Equal(expected, span.Length == 0);
        }

        [Theory]
        [InlineData(0, 0, "[0..0]")]
        [InlineData(0, 123, "[0..123]")]
        [InlineData(123, 100, "[123..223]")]
        public void ToString_ReturnsTheCorrectString(Int32 start, Int32 length, String expected)
        {
            var span = new TextSpan(start, length);
            Assert.Equal(expected, span.ToString());
        }
    }
}
