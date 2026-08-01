using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenTriviaTests
    {
        [Theory]
        [InlineData(SyntaxKind.CommentTrivia, "hello world")]
        [InlineData(SyntaxKind.WhitespaceTrivia, " ")]
        public void Constructor_SetsKindAndText(SyntaxKind kind, String text)
        {
            var trivia = new GreenTrivia(kind, text);
            Assert.Equal(kind, trivia.Kind);
            Assert.Equal(text, trivia.Text);
        }

        [Theory]
        [InlineData("abc123")]
        [InlineData("hello world")]
        public void Width_EqualsTextWidth(String text)
        {
            var trivia = new GreenTrivia(SyntaxKind.None, text);
            Assert.Equal(text.Length, trivia.Width);
        }

        [Theory]
        [InlineData("hello")]
        [InlineData("world")]
        public void ToString_ReturnsTriviaText(String text)
        {
            var trivia = new GreenTrivia(SyntaxKind.None, text);
            Assert.Equal(text, trivia.ToString());
        }
    }
}
