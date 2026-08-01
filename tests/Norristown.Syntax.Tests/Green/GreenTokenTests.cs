using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenTokenTests
    {
        [Fact]
        public void Constructor_SetsKindTriviaTextAndAttributes()
        {
            var trivia = new GreenTrivia[] { new(SyntaxKind.WhitespaceTrivia, " ") };
            var token = new GreenToken(SyntaxKind.NewlineToken, trivia, "\r\n", GreenTokenAttributes.IsPrecededByWhitespace);

            Assert.Equal(SyntaxKind.NewlineToken, token.Kind);
            Assert.Same(trivia, token.Trivia);
            Assert.Equal("\r\n", token.Text);
            Assert.Equal(GreenTokenAttributes.IsPrecededByWhitespace, token.Attributes);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(+1)]
        public void GetSlot_ReturnsNull(Int32 index)
        {
            var token = new GreenToken(SyntaxKind.NewlineToken, null, "\n", GreenTokenAttributes.None);
            Assert.Null(token.GetSlot(index));
        }

        [Theory]
        [InlineData("test")]
        [InlineData("hello world")]
        public void Width_EqualsTextLength(String text)
        {
            var token = new GreenToken(SyntaxKind.None, null, text, GreenTokenAttributes.None);
            Assert.Equal(text.Length, token.Width);
        }

        [Fact]
        public void TriviaWidth_EqualsSumOfTriviaWidths()
        {
            var trivia = new GreenTrivia[]
            {
                new(SyntaxKind.CommentTrivia, "hello"),
                new(SyntaxKind.CommentTrivia, " "),
                new(SyntaxKind.CommentTrivia, "world"),
            };
            var token = new GreenToken(SyntaxKind.None, trivia, "this is a test", GreenTokenAttributes.None);
            Assert.Equal("hello world".Length, token.TriviaWidth);
        }

        [Theory]
        [InlineData("test")]
        [InlineData("hello world")]
        public void FullWidth_EqualsTextLengthPlusTriviaWidth(String text)
        {
            var trivia = new GreenTrivia[] 
            {
                new(SyntaxKind.CommentTrivia, "hello"),
                new(SyntaxKind.CommentTrivia, " "),
                new(SyntaxKind.CommentTrivia, "world"),
            };
            var token = new GreenToken(SyntaxKind.None, trivia, text, GreenTokenAttributes.None);
            Assert.Equal(text.Length + "hello world".Length, token.FullWidth);
        }

        [Fact]
        public void IsMissing_MatchesAttributes()
        {
            var token1 = new GreenToken(SyntaxKind.None, null, "hello world", GreenTokenAttributes.None);
            Assert.False(token1.IsMissing);

            var token2 = new GreenToken(SyntaxKind.None, null, "hello world", GreenTokenAttributes.IsMissing);
            Assert.True(token2.IsMissing);
        }

        [Fact]
        public void IsPrecededByWhitespace_MatchesAttributes()
        {
            var token1 = new GreenToken(SyntaxKind.None, null, "hello world", GreenTokenAttributes.None);
            Assert.False(token1.IsPrecededByWhitespace);

            var token2 = new GreenToken(SyntaxKind.None, null, "hello world", GreenTokenAttributes.IsPrecededByWhitespace);
            Assert.True(token2.IsPrecededByWhitespace);
        }

        [Fact]
        public void IsToken_IsTrue()
        {
            var token = new GreenToken(SyntaxKind.None, null, "hello world", GreenTokenAttributes.None);
            Assert.True(token.IsToken);
        }

        [Theory]
        [InlineData("this is a test")]
        [InlineData("goodbye world")]
        public void ToFullString_ReturnsTextPlusTrivia(String text)
        {
            var trivia = new GreenTrivia[]
            {
                new(SyntaxKind.CommentTrivia, "hello"),
                new(SyntaxKind.CommentTrivia, " "),
                new(SyntaxKind.CommentTrivia, "world"),
                new(SyntaxKind.CommentTrivia, " "),
            };
            var token = new GreenToken(SyntaxKind.None, trivia, text, GreenTokenAttributes.None);
            Assert.Equal($"hello world {text}", token.ToFullString());
        }
    }
}
