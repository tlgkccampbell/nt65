using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenNodeFactoryTests
    {
        [Fact]
        public void Token_CreatesAToken()
        {
            var trivia = new GreenTrivia[] { new(SyntaxKind.WhitespaceTrivia, " ") };
            var node = GreenNodeFactory.Token(SyntaxKind.NewlineToken, trivia, "\r\n", GreenTokenAttributes.IsPrecededByWhitespace);

            Assert.IsType<GreenToken>(node);

            var token = (GreenToken)node;
            Assert.Equal(SyntaxKind.NewlineToken, token.Kind);
            Assert.Same(trivia, token.Trivia);
            Assert.Equal("\r\n", token.Text);
            Assert.Equal(GreenTokenAttributes.IsPrecededByWhitespace, token.Attributes);
        }

        [Fact]
        public void Node_CreatesASyntaxNode()
        {
            var child = new GreenToken(SyntaxKind.NewlineToken, null, "\r\n", GreenTokenAttributes.None);
            var node = GreenNodeFactory.Node(SyntaxKind.SkippedTokensTrivia, [child]);

            Assert.IsType<GreenSyntaxNode>(node);

            var syntaxNode = (GreenSyntaxNode)node;
            Assert.Equal(SyntaxKind.SkippedTokensTrivia, syntaxNode.Kind);
            Assert.Same(child, syntaxNode.GetSlot(0));
        }

        [Fact]
        public void Trivia_CreatesTrivia()
        {
            var trivia = GreenNodeFactory.Trivia(SyntaxKind.CommentTrivia, "this is a test");
            Assert.Equal(SyntaxKind.CommentTrivia, trivia.Kind);
            Assert.Equal("this is a test", trivia.Text);
        }
    }
}
