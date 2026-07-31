using Norristown.Analysis.Text;

namespace Norristown.Analysis.Tests.Text
{
    public sealed class TextChangeTests
    {
        [Fact]
        public void DefaultValue_HasExpectedProperties()
        {
            var change = default(TextChange);
            Assert.Equal(0, change.Span.Start);
            Assert.Equal(0, change.Span.Length);
            Assert.Equal(String.Empty, change.NewText);
        }

        [Theory]
        [InlineData(0, 0, "")]
        [InlineData(123, 234, "some new text")]
        public void Constructor_SetsSpanAndNewText(Int32 start, Int32 length, String newText)
        {
            var change = new TextChange(new(start, length), newText);
            Assert.Equal(start, change.Span.Start);
            Assert.Equal(length, change.Span.Length);
            Assert.Equal(newText, change.NewText);
        }

        [Theory]
        [InlineData(0, 0, "", "[0..0] -> \"\"")]
        [InlineData(123, 234, "new text", "[123..357] -> \"new text\"")]
        public void ToString_ReturnsTheCorrectString(Int32 start, Int32 length, String newText, String expected)
        {
            var change = new TextChange(new(start, length), newText);
            Assert.Equal(expected, change.ToString());
        }
    }
}
