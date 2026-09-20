using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// What a comma-separated list holds where the commas are not written as the grammar wants
/// them. A separated list alternates an item and the comma after it, so the parser takes a
/// comma only after an item it already has, and the first item it cannot read ends the list:
/// the tokens past that are the line's, as any other leftovers are. An item that is an
/// expression is always there, because the parser leaves the empty expression where it could
/// read none, so a list of expressions keeps a place for every comma written.
/// </summary>
public sealed class SeparatedListParsingTests
{
    [Theory]
    // The plain cases: n items and the n − 1 commas between them.
    [InlineData("f(1, 2)", 2, 1)]
    [InlineData("f(1)", 1, 0)]
    [InlineData("f()", 0, 0)]
    // An expression the parser cannot read is the empty expression, which stands as the item.
    [InlineData("f(1,)", 2, 1)]
    [InlineData("f(,1)", 2, 1)]
    [InlineData("f(1,,2)", 3, 2)]
    [InlineData("f(1, 2,)", 3, 2)]
    // A list cut off after a comma keeps the comma, and the item after it is empty.
    [InlineData("f(1,", 2, 1)]
    public void AListOfExpressionsKeepsAPlaceForEveryComma(string call, int items, int separators)
    {
        var arguments = Call(call).Arguments.Arguments;
        Assert.Equal(items, arguments.Count);
        Assert.Equal(separators, arguments.SeparatorCount);
        for (var i = 0; i < separators; i++)
            Assert.Equal(SyntaxKind.Comma, arguments.GetSeparator(i).Kind);
    }

    /// <summary>
    /// A list whose items are written some other way has no item to stand between two commas,
    /// so it ends at the gap, the comma before it is its last piece, and the rest of the line
    /// is what the line holds as it holds any other leftovers.
    /// </summary>
    [Theory]
    [InlineData(".export a, b", 2, 1, "")]
    [InlineData(".export a,", 1, 1, "")]
    [InlineData(".export a,,b", 1, 1, ",b")]
    [InlineData(".export a, b,,c", 2, 2, ",c")]
    [InlineData(".export ,a", 0, 0, ",a")]
    [InlineData(".export", 0, 0, "")]
    public void AListOfWrittenItemsEndsAtTheFirstGap(string source, int items, int separators, string left)
    {
        var line = Line(source);
        var export = Assert.IsType<ExportDirectiveSyntax>(line.Statement);
        Assert.Equal(items, export.Items.Count);
        Assert.Equal(separators, export.Items.SeparatorCount);
        Assert.Equal(left, line.SkippedTokens?.GetText() ?? "");
    }

    /// <summary>
    /// The items and the commas are the holder's own children, in source order, so a walk of the
    /// tree meets them where the list itself is and never meets the node over the list.
    /// </summary>
    [Fact]
    public void AHoldersChildrenAreItsItemsAndItsSeparators()
    {
        var arguments = Call("f(1, 2)").Arguments;
        Assert.Equal(
            ["(", "1", ",", "2", ")"],
            arguments.ChildNodesAndTokens().Select(child => child.ToFullString().Trim()));
        Assert.Equal(["(", ",", ")"], arguments.ChildTokens.Select(token => token.Text));
        Assert.Equal(["1", "2"], arguments.ChildNodes.Select(node => node.GetText()));
    }

    /// <summary>A list with no items is one slot holding nothing, and reads as no items.</summary>
    [Fact]
    public void AnEmptyListIsASlotHoldingNothing()
    {
        var arguments = Call("f()").Arguments;
        Assert.Null(arguments.Green.GetSlot(1));
        Assert.Empty(arguments.Arguments);
        Assert.Empty(arguments.Arguments.GetSeparators());
        Assert.Equal(["(", ")"], arguments.ChildNodesAndTokens().Select(child => child.ToFullString()));
    }

    /// <summary>The <c>)</c> a list is not closed with stands in its slot, missing.</summary>
    [Fact]
    public void AnUnclosedListStandsItsClosingParenthesis()
    {
        var arguments = Call("f(1,").Arguments;
        Assert.True(arguments.CloseParenToken.IsMissing);
        Assert.Equal("", arguments.CloseParenToken.Text);
        Assert.False(Call("f(1)").Arguments.CloseParenToken.IsMissing);
    }

    /// <summary>A <c>.func</c>'s parameters and a <c>one(...)</c>'s words are nodes, as every item is.</summary>
    [Fact]
    public void AnItemWrittenAsOneWordIsStillANode()
    {
        var func = Assert.IsType<FuncDeclarationSyntax>(Line(".func f(a, b) = a").Statement);
        Assert.Equal(["a", "b"], func.Parameters!.Parameters.Select(parameter => parameter.Name.Text));

        var macro = Assert.IsType<MacroDeclarationSyntax>(Line(".macro m(p: one(lda, sta)) {").Statement);
        var kind = macro.Parameters!.Parameters[0].ParameterKind!;
        Assert.Equal(["lda", "sta"], kind.Words.Select(word => word.Name.Text));
        Assert.Equal(1, kind.Words.SeparatorCount);
    }

    private static CallExpressionSyntax Call(string call) =>
        Assert.Single(Assert.IsType<DataDirectiveSyntax>(Line(".word " + call).Statement).Values)
            as CallExpressionSyntax ?? throw new InvalidOperationException($"`{call}` is no call");

    private static LineSyntax Line(string source) =>
        SyntaxTree.Parse("test.nt65", source).Root.DescendantNodes().OfType<LineSyntax>().First();
}
