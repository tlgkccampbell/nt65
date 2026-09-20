using Norristown.Syntax;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Tests.Syntax;

public sealed class SyntaxListTests
{
    [Fact]
    public void ASeparatedListReadsItsItemsAndItsSeparators()
    {
        var items = Arguments("(a, b)");
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items.SeparatorCount);
        Assert.Equal(["a", "b"], items.Select(item => item.GetText()));
        Assert.Equal(new TextSpan(1, 1), items[0].Span);
        Assert.Equal(new TextSpan(4, 1), items[1].Span);
        Assert.Equal(",", items.GetSeparator(0).Text);
        Assert.Equal(new TextSpan(2, 1), items.GetSeparator(0).Span);
        Assert.Equal([","], items.GetSeparators().Select(separator => separator.Text));
    }

    [Fact]
    public void AnItemAndASeparatorAreOutOfRangeBeyondTheirOwnCount()
    {
        var items = Arguments("(a, b)");
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = items[2]; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = items.GetSeparator(1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = items[-1]; });
    }

    [Fact]
    public void TheItemsAndSeparatorsReadTogetherInSourceOrder()
    {
        var withSeparators = Arguments("(a, b)").GetWithSeparators();
        Assert.Equal(3, withSeparators.Count);
        Assert.Equal([SyntaxKind.NameExpression, SyntaxKind.Comma, SyntaxKind.NameExpression],
            withSeparators.Select(child => child.Kind));
        Assert.Equal(["a", ", ", "b"], withSeparators.Select(child => child.ToFullString()));
    }

    [Fact]
    public void ASeparatedListMayEndWithASeparator()
    {
        var items = Arguments("(a, b,)");
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.SeparatorCount);
        Assert.Equal(4, items.GetWithSeparators().Count);
        Assert.Equal(new TextSpan(5, 1), items.GetSeparator(1).Span);
        Assert.Equal(["a", "b"], items.Select(item => item.GetText()));
    }

    [Fact]
    public void AListWithoutSeparatorsReadsItsItems()
    {
        var tokens = HandBuilt.Tokens("(a b)");
        var parent = HandBuilt.Node("(a b)", SyntaxKind.ArgumentList, tokens[0],
            new GreenList([HandBuilt.Name(tokens[1]), HandBuilt.Name(tokens[2])]), tokens[3]);
        var items = HandBuilt.List<NameExpressionSyntax>(parent, 1);
        Assert.Equal(2, items.Count);
        Assert.Equal(["a", "b"], items.Select(item => item.GetText()));
        Assert.Equal(new TextSpan(3, 1), items[1].Span);

        // The list's own node covers its items and is where their red nodes are kept, but the
        // node holding the list shows the items themselves and is their parent.
        Assert.Equal(new TextSpan(1, 3), parent.SlotRed(1)!.Span);
        Assert.Same(items[0], items[0]);
        Assert.Equal([items[0], items[1]], parent.ChildNodes);
        Assert.Same(parent, items[0].Parent);
    }

    [Fact]
    public void ATokenListReadsTheTokensOfAList()
    {
        var tokens = HandBuilt.Tokens("nop lda");
        var parent = HandBuilt.Node("nop lda", SyntaxKind.ErrorLine, new GreenList([tokens[0], tokens[1]]));
        var list = HandBuilt.TokenList(parent, 0);
        Assert.Equal(2, list.Count);
        Assert.Equal(["nop", "lda"], list.Select(token => token.Text));
        Assert.Equal(new TextSpan(4, 3), list[1].Span);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = list[2]; });
    }

    [Fact]
    public void AnEmptyListHasNoItems()
    {
        Assert.Equal(0, default(SyntaxList<SyntaxNode>).Count);
        Assert.Empty(default(SyntaxList<SyntaxNode>));
        Assert.Equal(0, default(SyntaxTokenList).Count);
        Assert.Empty(default(SyntaxTokenList));

        var separated = default(SeparatedSyntaxList<SyntaxNode>);
        Assert.Equal(0, separated.Count);
        Assert.Equal(0, separated.SeparatorCount);
        Assert.Empty(separated);
        Assert.Empty(separated.GetSeparators());
        Assert.Equal(0, separated.GetWithSeparators().Count);
    }

    [Fact]
    public void AListNodeWithNothingInItReadsAsAnEmptyList()
    {
        var tokens = HandBuilt.Tokens("()");
        var parent = HandBuilt.Node("()", SyntaxKind.ArgumentList, tokens[0], new GreenList([]), tokens[1]);
        Assert.Equal(0, HandBuilt.List<SyntaxNode>(parent, 1).Count);
        Assert.Equal(0, HandBuilt.SeparatedList<SyntaxNode>(parent, 1).Count);
        Assert.Equal(0, HandBuilt.TokenList(parent, 1).Count);

        // The empty list sits where its items would have been written, and takes up nothing.
        Assert.Equal(new TextSpan(1, 0), parent.SlotRed(1)!.Span);
        Assert.Empty(parent.ChildNodes);
        Assert.Equal("()", parent.ToFullString());
    }

    [Fact]
    public void ListsMatchListAndSlicePatterns()
    {
        var items = Arguments("(a, b, c)");
        Assert.True(items is [_, _, _]);
        Assert.True(items is [var first, ..] && first.GetText() == "a");
        var rest = items is [_, .. var tail] ? tail : [];
        Assert.Equal(["b", "c"], rest.Select(item => item.GetText()));
        Assert.Equal(["a", "b"], items.Slice(0, 2).Select(item => item.GetText()));

        Assert.True(Arguments("(a)") is [{ } only] && only.GetText() == "a");
        Assert.True(items.GetWithSeparators() is [_, _, _, _, _]);

        var tokens = HandBuilt.Tokens("nop lda");
        var parent = HandBuilt.Node("nop lda", SyntaxKind.ErrorLine, new GreenList([tokens[0], tokens[1]]));
        Assert.True(HandBuilt.TokenList(parent, 0) is [_, { Text: "lda" }]);
    }

    [Fact]
    public void ASeparatedListAlternatesItemNodesAndSeparatorTokens()
    {
        var tokens = HandBuilt.Tokens("a, b");
        var name = HandBuilt.Name(tokens[0]);

        // An item where a separator belongs, and a token where an item belongs.
        Assert.Throws<ArgumentException>(() => { _ = new GreenSeparatedList([name, name]); });
        Assert.Throws<ArgumentException>(() => { _ = new GreenSeparatedList([tokens[0]]); });

        var list = new GreenSeparatedList([name, tokens[1], HandBuilt.Name(tokens[2])]);
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list.SeparatorCount);
        Assert.Same(name, list.Item(0));
        Assert.Same(tokens[1], list.Separator(0));
    }

    [Fact]
    public void ABuilderWithNothingInItMakesNoNode()
    {
        var builder = new GreenListBuilder();
        Assert.Equal(0, builder.Count);
        Assert.Null(builder.ToList());
        Assert.Null(builder.ToSeparatedList());
    }

    /// <summary>
    /// The items of an argument list built from <paramref name="text"/>: every token between
    /// the parentheses becomes an item, and every comma the separator after one.
    /// </summary>
    private static SeparatedSyntaxList<NameExpressionSyntax> Arguments(string text)
    {
        var tokens = HandBuilt.Tokens(text);
        var builder = new GreenListBuilder();
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Kind == SyntaxKind.Comma)
                builder.AddSeparator(tokens[i]);
            else
                builder.Add(HandBuilt.Name(tokens[i]));
        }
        var node = HandBuilt.Node(text, SyntaxKind.ArgumentList, tokens[0], builder.ToSeparatedList()!, tokens[^1]);
        return HandBuilt.SeparatedList<NameExpressionSyntax>(node, 1);
    }
}
