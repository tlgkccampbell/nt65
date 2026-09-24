using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class TriviaTests
{
    [Fact]
    public void LeadingTriviaIsTheWhitespaceBeforeTheFirstTokenOfALine()
    {
        var tokens = Tokens("  lda $10");
        var leading = tokens[0].LeadingTrivia;
        Assert.Equal(1, leading.Count);
        Assert.Equal(SyntaxKind.WhitespaceTrivia, leading[0].Kind);
        Assert.Equal("  ", leading[0].Text);
        Assert.Equal("  ", leading[0].ToString());
        Assert.Equal(new TextSpan(0, 2), leading[0].Span);
        Assert.Equal(tokens[0], leading[0].Token);
        Assert.Equal(tokens[0], leading.Token);

        // Only the first token of a line has any.
        Assert.Empty(tokens[1].LeadingTrivia);
    }

    [Fact]
    public void TrailingTriviaIsTheWhitespaceAndCommentAfterAToken()
    {
        var tokens = Tokens("  lda $10  ; note");
        Assert.Equal([" "], tokens[0].TrailingTrivia.Select(trivia => trivia.Text));
        Assert.Equal(new TextSpan(5, 1), tokens[0].TrailingTrivia[0].Span);

        var trailing = tokens[1].TrailingTrivia;
        Assert.Equal(2, trailing.Count);
        Assert.Equal([SyntaxKind.WhitespaceTrivia, SyntaxKind.CommentTrivia], trailing.Select(trivia => trivia.Kind));
        Assert.Equal(new TextSpan(9, 2), trailing[0].Span);
        Assert.Equal("; note", trailing[1].Text);
        Assert.Equal(new TextSpan(11, 6), trailing[1].Span);
        Assert.Equal(["  ", "; note"], trailing.Slice(0, 2).Select(trivia => trivia.Text));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = trailing[2]; });
    }

    [Fact]
    public void TriviaIsPlacedFromTheStartOfTheFileNotOfItsLine()
    {
        var tokens = Tokens("nop\n  lda $10", 1);
        Assert.Equal(new TextSpan(4, 2), tokens[0].LeadingTrivia[0].Span);
        Assert.Equal("  ", tokens[0].Parent.Tree.Text.Substring(4, 2));
    }

    [Fact]
    public void ALineWithOnlyACommentHangsItOnTheLineBreak()
    {
        var tree = SyntaxTree.Parse("test.nt65", "  ; just a comment");
        var line = (LineSyntax)tree.Root.ChildNodes[0];
        var end = line.ChildTokens[^1];
        Assert.Equal(SyntaxKind.EndOfLine, end.Kind);
        Assert.Equal([SyntaxKind.WhitespaceTrivia, SyntaxKind.CommentTrivia],
            end.LeadingTrivia.Select(trivia => trivia.Kind));
        Assert.Equal(new TextSpan(2, 16), end.LeadingTrivia[1].Span);
        Assert.Empty(end.TrailingTrivia);
    }

    /// <summary>
    /// Returns the tokens of line <paramref name="line"/> of <paramref name="text"/>, without its
    /// line break.
    /// </summary>
    private static List<SyntaxToken> Tokens(string text, int line = 0)
    {
        var tree = SyntaxTree.Parse("test.nt65", text);
        var lines = tree.Root.DescendantNodes().OfType<LineSyntax>().ToList();
        return [.. lines[line].ChildTokens.Where(token => token.Kind != SyntaxKind.EndOfLine)];
    }
}
