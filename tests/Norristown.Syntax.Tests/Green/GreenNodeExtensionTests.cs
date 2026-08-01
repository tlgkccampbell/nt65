using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenNodeExtensionTests
    {
        [Fact]
        public void SumFullWidths_CorrectlySumsNodeWidths()
        {
            var trivia = new GreenTrivia[]
            {
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "this"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "is"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "a"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "test"),
                new(SyntaxKind.None, " "),
            };

            var nodes = new GreenNode?[]
            {
                new GreenToken(SyntaxKind.None, null, "start", GreenTokenAttributes.None),
                new GreenToken(SyntaxKind.None, trivia, "middle", GreenTokenAttributes.None),
                null,
                new GreenToken(SyntaxKind.None, null, "end", GreenTokenAttributes.None),
            };

            var width = nodes.SumFullWidths();
            Assert.Equal("start this is a test middle end".Length, width);
        }
    }
}
