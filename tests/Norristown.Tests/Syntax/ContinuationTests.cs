using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks how lines join where an expression's bracket stays open: which lines join, which stop
/// a join, where a line break is allowed, and what an edit that opens or closes a bracket does.
/// </summary>
public sealed class ContinuationTests
{
    [Fact]
    public void ALineWithAnOpenBracketContinuesOntoTheNext()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = (1 +\n    2)\n.word X\n");

        Assert.Same(tree.GetLine(0), tree.GetLine(1));
        Assert.NotSame(tree.GetLine(1), tree.GetLine(2));
        Assert.Equal(SyntaxKind.ConstantDeclaration, tree.GetLine(1).Statement.Kind);
        Assert.Equal(0, tree.GetLine(1).LineIndex);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ALineOfOnlyACommentStaysInTheExpression()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = .select(1,   ; which\n    ; the first\n    2, 3)\n");

        Assert.Same(tree.GetLine(0), tree.GetLine(2));
        Assert.Empty(tree.Diagnostics);
    }

    [Theory]
    [InlineData(".const X = (1\n\n    2)\n")]
    [InlineData(".const X = (1\n    lda #2\n")]
    [InlineData(".const X = (1\n.byte 2\n")]
    [InlineData(".const X = (1\n}\n")]
    [InlineData(".const X = (1\n.const Y = 2\n")]
    [InlineData(".const X = (1\nthere:\n")]
    public void ALineThatStartsAStatementIsNotJoined(string text)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        Assert.NotSame(tree.GetLine(0), tree.GetLine(1));
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Id == "expected-parenthesis");
    }

    [Theory]
    [InlineData(".proc main {\n    lda (ptr\n    ),y\n}\n")]
    [InlineData(".proc main {\n    pair!(1,\n        2)\n}\n")]
    public void OnlyAnExpressionsBracketsHoldALineBreak(string text)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("continuation-outside-expression", diagnostic.Id);
        Assert.Equal(2, diagnostic.Span.Line);
    }

    [Fact]
    public void ClosingTheBracketSplitsTheLinesAgain()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = (1 +\n    2)\n");
        var edited = tree.WithChange(new TextChange(11, 1, ""));

        Assert.NotSame(edited.GetLine(0), edited.GetLine(1));
        Assert.Equal(SyntaxDump.Full(SyntaxTree.Parse("main.nt65", edited.Text)), SyntaxDump.Full(edited));
    }

    [Fact]
    public void OpeningABracketJoinsTheLinesAfterIt()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = 1 +\n    2)\n");
        var edited = tree.WithChange(new TextChange(11, 0, "("));

        Assert.Same(edited.GetLine(0), edited.GetLine(1));
        Assert.Empty(edited.Diagnostics);
    }

    [Fact]
    public void AnEditElsewhereKeepsAJoinedLine()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = (1 +\n    2)\n.const Y = 3\n");
        var edited = tree.WithChange(new TextChange(tree.Text.IndexOf('3'), 1, "4"));

        Assert.Same(tree.Parsed(0).Node, edited.Parsed(0).Node);
    }

    [Fact]
    public void ADiagnosticOnAContinuedLineIsOnThatLine()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".const X = (1 +\n    2 $)\n");

        Assert.NotEmpty(tree.Diagnostics);
        Assert.All(tree.Diagnostics, diagnostic =>
        {
            Assert.Equal(2, diagnostic.Span.Line);
            Assert.Equal(7, diagnostic.Span.StartColumn);
        });
    }
}
