using Norristown.Syntax.Text;

namespace Norristown.Syntax.Tests.Text
{
    public sealed partial class SourceTextTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        public void From_CreatesSourceText(String text)
        {
            var sourceText = SourceText.From(text);
            Assert.Equal(text, sourceText.ToString());
            Assert.Equal(0, sourceText.Revision);
        }

        [Theory]
        [InlineData("", 0, 0)]
        [InlineData("abc", 1, 1)]
        [InlineData("this is a test", 0, 14)]
        public void CopyTo_WhenTextFitsInBuffer_CopiesTextToBuffer(String text, Int32 start, Int32 length)
        {
            var sourceText = SourceText.From(text);
            var buffer = new Char[length];
            sourceText.CopyTo(new(start, length), buffer);
            Assert.Equal(new String(buffer), text[start..(start + length)]);
        }

        [Theory]
        [InlineData("abc", 1, 1)]
        [InlineData("this is a test", 0, 14)]
        public void CopyTo_WhenTextDoesNotFitInBuffer_ThrowsArgumentException(String text, Int32 start, Int32 length)
        {
            var sourceText = SourceText.From(text);
            var buffer = Array.Empty<Char>();
            Assert.Throws<ArgumentException>(() =>
            {
                sourceText.CopyTo(new(start, length), buffer);
            });
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(999999)]
        public void GetLineIndex_WhenPositionIsInvalid_ThrowsArgumentOutOfRangeException(Int32 position)
        {
            var sourceText = SourceText.From("hello world");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.GetLineIndex(position);
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(11, 0)]
        [InlineData(12, 1)]
        [InlineData(18, 1)]
        [InlineData(26, 1)]
        [InlineData(27, 2)]
        [InlineData(55, 2)]
        public void GetLineIndex_WhenPositionIsValid_ReturnsLineIndex(Int32 position, Int32 expected)
        {
            var sourceText = SourceText.From("hello world\nthis is a test\nof the GetLineIndex() method");
            var lineIndex = sourceText.GetLineIndex(position);
            Assert.Equal(expected, lineIndex);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        public void GetLineStart_WhenLineIndexIsInvalid_ThrowsArgumentOutOfRangeException(Int32 lineIndex)
        {
            var sourceText = SourceText.From("hello world\nthis is a test\nof the GetLineIndex() method");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.GetLineStart(lineIndex);
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 12)]
        [InlineData(2, 27)]
        public void GetLineStart_WhenLineIndexIsValid_ReturnsTheStartingPosition(Int32 lineIndex, Int32 expected)
        {
            var sourceText = SourceText.From("hello world\nthis is a test\nof the GetLineIndex() method");
            var lineStart = sourceText.GetLineStart(lineIndex);
            Assert.Equal(expected, lineStart);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(28)]
        public void GetLineSpan_WhenLineIndexIsInvalid_ThrowsArgumentOutOfRangeException(Int32 lineIndex)
        {
            var sourceText = SourceText.From("hello world\nthis is a test\nof the GetLineIndex() method");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.GetLineSpan(lineIndex);
            });
        }


        [Fact]
        public void GetLineSpan_WhenLineIndexIsValid_ReturnsTheCorrectSpan()
        {
            const String Text = "hello world\nthis is a test\nof the GetLineIndex() method";

            var sourceText = SourceText.From(Text);
            var sourceTextLines = Text.Split('\n');

            var lineSpan0 = sourceText.GetLineSpan(0);
            Assert.Equal(sourceTextLines[0], sourceText.ToString(lineSpan0));

            var lineSpan1 = sourceText.GetLineSpan(1);
            Assert.Equal(sourceTextLines[1], sourceText.ToString(lineSpan1));

            var lineSpan2 = sourceText.GetLineSpan(2);
            Assert.Equal(sourceTextLines[2], sourceText.ToString(lineSpan2));
        }

        [Theory]
        [InlineData("", 0, 0, 0)]
        [InlineData("\r", 0, 0, 0)]
        [InlineData("\n", 0, 0, 0)]
        [InlineData("\r\n", 0, 0, 0)]
        [InlineData("abc\ntest\n", 1, 4, 4)]
        [InlineData("abc\n", 1, 4, 0)]
        public void GetLineSpan_WhenLineIndexIsValid_HandlesNewlines(String text, Int32 lineIndex, Int32 start, Int32 length)
        {
            var sourceText = SourceText.From(text);
            var lineSpan = sourceText.GetLineSpan(lineIndex);

            Assert.Equal(new TextSpan(start, length), lineSpan);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(28)]
        public void GetLineSpanIncludingLineBreak_WhenLineIndexIsInvalid_ThrowsArgumentOutOfRangeException(Int32 lineIndex)
        {
            var sourceText = SourceText.From("hello world\nthis is a test\nof the GetLineIndex() method");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.GetLineSpanIncludingLineBreak(lineIndex);
            });
        }

        [Fact]
        public void GetLineSpanIncludingLineBreak_WhenLineIndexIsValid_ReturnsTheCorrectSpan()
        {
            const String Text = "hello world\nthis is a test\nof the GetLineIndex() method";

            var sourceText = SourceText.From(Text);
            var sourceTextLines = Text.Split('\n');

            var lineSpan0 = sourceText.GetLineSpanIncludingLineBreak(0);
            Assert.Equal($"{sourceTextLines[0]}\n", sourceText.ToString(lineSpan0));

            var lineSpan1 = sourceText.GetLineSpanIncludingLineBreak(1);
            Assert.Equal($"{sourceTextLines[1]}\n", sourceText.ToString(lineSpan1));

            var lineSpan2 = sourceText.GetLineSpanIncludingLineBreak(2);
            Assert.Equal($"{sourceTextLines[2]}", sourceText.ToString(lineSpan2));
        }

        [Fact]
        public void WithChange_WhenChangeIsTooLong_ThrowsArgumentOutOfRangeException()
        {
            var sourceText = SourceText.From("test");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.WithChange(new(new(0, 100), "new text"));
            });
        }

        [Fact]
        public void WithChange_WhenChangeIsValid_AppliesTheChange()
        {
            var sourceText = SourceText.From("hello world");
            var updatedSourceText = sourceText.WithChange(new(new(0, 5), "goodbye"));

            Assert.Equal(1L, updatedSourceText.Revision);
            Assert.Equal("goodbye world", updatedSourceText.ToString());
        }

        [Fact]
        public void WithChanges_WhenChangeSetIsEmpty_ReturnsTheOriginalText()
        {
            var sourceText = SourceText.From("hello world");
            var updatedSourceText = sourceText.WithChanges();

            Assert.Equal(0L, updatedSourceText.Revision);
            Assert.Same(sourceText, updatedSourceText);
        }

        [Fact]
        public void WithChanges_WhenChangeSetIsValid_AppliesTheChanges()
        {
            var sourceText = SourceText.From("hello world");
            var updatedSourceText = sourceText.WithChanges(
            [
                new TextChange(new(6, 5), "universe"),
                new TextChange(new(0, 5), "goodbye"),
            ]);

            Assert.Equal(0L, sourceText.Revision);
            Assert.Equal("hello world", sourceText.ToString());

            Assert.Equal(1L, updatedSourceText.Revision);
            Assert.Equal("goodbye universe", updatedSourceText.ToString());
        }

        [Fact]
        public void WithChanges_WhenChangeIsTooLong_ThrowsArgumentOutOfRangeException()
        {
            var sourceText = SourceText.From("test");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                sourceText.WithChanges([new(new(0, 100), "new text")]);
            });
        }

        [Fact]
        public void WithChanges_WhenChangesOverlap_ThrowsArgumentException()
        {
            var sourceText = SourceText.From("test");
            Assert.Throws<ArgumentException>(() =>
            {
                sourceText.WithChanges(
                [
                    new(new(0, 2), "new text"),
                    new(new(1, 2), "another edit"),
                ]);
            });
        }

        [Theory]
        [InlineData("hello world", 0, 0, "")]
        [InlineData("hello world", 0, 5, "hello")]
        public void ToString_WithSpan_ReturnsCorrectString(String text, Int32 start, Int32 length, String expected)
        {
            var sourceText = SourceText.From(text);
            Assert.Equal(expected, sourceText.ToString(new(start, length)));
        }

        [Fact]
        public void ToString_ReturnsCorrectString()
        {
            // We use a FakeSourceText here to bypass optimizations that override the base ToString(),
            // like the one implemented in StringSourceText.
            var sourceText = new FakeSourceText("hello world");
            Assert.Equal("hello world", sourceText.ToString());
        }
    }
}
