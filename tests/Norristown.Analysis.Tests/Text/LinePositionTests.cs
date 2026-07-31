using Norristown.Analysis.Text;

namespace Norristown.Analysis.Tests.Text
{
    public sealed class LinePositionTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(123, 234)]
        [InlineData(234, 345)]
        public void Constructor_SetsLineAndColumn(Int32 line, Int32 column)
        {
            var position = new LinePosition(line, column);
            Assert.Equal(line, position.Line);
            Assert.Equal(column, position.Column);
        }

        [Theory]
        [InlineData(0, 0, "(0,0)")]
        [InlineData(123, 234, "(123,234)")]
        public void ToString_ReturnsTheCorrectString(Int32 line, Int32 column, String expected)
        {
            var position = new LinePosition(line, column);
            Assert.Equal(expected, position.ToString());
        }
    }
}
