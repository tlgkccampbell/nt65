using Norristown.Analysis.Text;

namespace Norristown.Analysis.Tests.Text
{
    public sealed class LinePositionTests
    {
        [Fact]
        public void Constructor_SetsLineAndColumn()
        {
            var position = new LinePosition(123, 234);
            Assert.Equal(123, position.Line);
            Assert.Equal(234, position.Column);
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
