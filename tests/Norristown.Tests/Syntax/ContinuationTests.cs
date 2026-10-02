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
    [InlineData(".const X = .select(1,\n    count = 2)\n")]
    [InlineData(".proc main {\n    pair!(1\n    lda #2\n}\n")]
    public void ALineThatStartsAStatementIsNotJoined(string text)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        Assert.NotSame(tree.GetLine(0), tree.GetLine(1));
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Id == "expected-parenthesis");
    }

    /// <summary>
    /// Checks that a line shaped like a parameter's kind joins only where the innermost open
    /// bracket is a list of parameters, and only when a kind follows its <c>:</c>. A label alone
    /// or before an instruction still starts a statement there.
    /// </summary>
    [Theory]
    [InlineData(".proc main {\n    pair!(1,\n    second: expr)\n}\n")]
    [InlineData(".macro m(first,\nloop:\n    nop\n")]
    [InlineData(".macro m(first,\nloop: lda #1\n")]
    [InlineData(".macro m(first = (1,\n    second: expr)) {\n}\n")]
    [InlineData(".macro m(first,\n    lda #1\n")]
    public void ALineInParametersThatStartsAStatementIsNotJoined(string text)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        Assert.NotSame(tree.GetLine(0), tree.GetLine(1));
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Theory]
    [InlineData(".proc main {\n    lda (ptr\n    ),y\n}\n")]
    [InlineData(".proc main {\n    pair!({(ptr\n        ),y}, 2)\n}\n")]
    public void OnlyAnExpressionsBracketsHoldALineBreak(string text)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("continuation-outside-expression", diagnostic.Id);
        Assert.Equal(2, diagnostic.Span.Line);
    }

    /// <summary>
    /// Checks that a macro call's arguments continue as a call's do. A line inside them may start
    /// with a braced operand or a named argument, and a block argument after them opens its block.
    /// </summary>
    [Fact]
    public void AMacroCallsArgumentsContinue()
    {
        var tree = SyntaxTree.Parse("main.nt65",
            ".proc main {\n    pair!(   ; first\n        {ptr,x},\n        ; alone\n        second = 2) {\n        nop\n    }\n}\n");

        Assert.Same(tree.GetLine(1), tree.GetLine(4));
        Assert.Equal(SyntaxKind.MacroCall, tree.GetLine(1).Statement.Kind);
        Assert.Equal(BlockKind.MacroBlock, tree.GetLine(1).OpensBlockKind);
        Assert.NotSame(tree.GetLine(4), tree.GetLine(5));
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    /// Checks that a macro's parameters continue as a macro call's arguments do. A line inside
    /// them may start with a parameter's kind or its default, a kind's own parentheses may hold a
    /// break, and the <c>{</c> after them opens the macro's block.
    /// </summary>
    [Theory]
    [InlineData(".macro m(   ; first\n    a: operand(abs,\n        zp),\n    ; alone\n    b = 2) {\n    nop\n}\n", 4)]
    [InlineData(".export .macro m(\n    a,\n    b: ::gfx::Kind) {\n    nop\n}\n", 2)]
    [InlineData(".macro m(\n    a: const(0..\n        15) = 1,\n    body: block = {}\n) {\n    nop\n}\n", 4)]
    public void AMacrosParametersContinue(string text, int last)
    {
        var tree = SyntaxTree.Parse("main.nt65", text);

        Assert.Same(tree.GetLine(0), tree.GetLine(last));
        Assert.Equal(SyntaxKind.MacroDeclaration, tree.GetLine(0).Statement.Kind);
        Assert.Equal(BlockKind.Macro, tree.GetLine(0).OpensBlockKind);
        Assert.NotSame(tree.GetLine(last), tree.GetLine(last + 1));
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    /// Checks that a function's parameters continue, and that the <c>=</c> and the body after them
    /// belong to the same declaration.
    /// </summary>
    [Fact]
    public void AFunctionsParametersContinue()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".func f(\n    a,      ; the first\n    b) = a + b\n.word f(1, 2)\n");

        Assert.Same(tree.GetLine(0), tree.GetLine(2));
        var function = Assert.IsType<FuncDeclarationSyntax>(tree.GetLine(0).Statement);
        Assert.Equal(2, function.Parameters!.Parameters.Count);
        Assert.Equal("a + b", function.Body.GetText());
        Assert.NotSame(tree.GetLine(2), tree.GetLine(3));
        Assert.Empty(tree.Diagnostics);
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
