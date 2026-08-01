using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenTriviaExtensionsTests
    {
        [Fact]
        public void SumWidths_CorrectlySumsTriviaWidths()
        {
            var trivia = new GreenTrivia[] 
            {
                new(SyntaxKind.None, "this"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "is"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "a"),
                new(SyntaxKind.None, " "),
                new(SyntaxKind.None, "test"),
            };

            var width = trivia.SumWidths();
            Assert.Equal("this is a test".Length, width);
        }
    }
}
