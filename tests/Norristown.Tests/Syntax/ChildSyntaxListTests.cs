using System.Text;
using Norristown.Syntax;
using Green = Norristown.Syntax.InternalSyntax;
using GreenSeparatedList = Norristown.Syntax.InternalSyntax.GreenSeparatedList;

namespace Norristown.Tests.Syntax;

public sealed class ChildSyntaxListTests
{
    /// <summary>
    /// A slot holding a list shows the list's items and separators, not the node over them, so
    /// a walk of a node's children never meets one.
    /// </summary>
    [Fact]
    public void ChildrenAreNodesAndTokensTogether()
    {
        var tokens = HandBuilt.Tokens("(a, b)");
        var arguments = HandBuilt.Over("(a, b)", new Green.ArgumentListSyntax(tokens[0],
            new GreenSeparatedList([HandBuilt.Name(tokens[1]), tokens[2], HandBuilt.Name(tokens[3])]), tokens[4]));

        var children = arguments.ChildNodesAndTokens();
        Assert.Equal(5, children.Count);
        Assert.Equal(
            [
                SyntaxKind.OpenParen, SyntaxKind.IdentifierName, SyntaxKind.Comma,
                SyntaxKind.IdentifierName, SyntaxKind.CloseParen,
            ],
            children.Select(child => child.Kind));
        Assert.True(children[0].IsToken);
        Assert.False(children[0].IsNode);
        Assert.Equal("(", children[0].AsToken().Text);
        Assert.Null(children[0].AsNode());
        Assert.True(children[1].IsNode);
        Assert.False(children[1].IsToken);
        Assert.Equal("a", children[1].ToFullString());
        Assert.Equal(new TextSpan(1, 1), children[1].Span);
        Assert.Same(arguments, children[1].AsNode()!.Parent);
        Assert.Same(arguments, children[2].AsToken().Parent);
        Assert.Same(arguments, children[4].AsToken().Parent);

        // A child that is a node is the red node the list keeps, not a new one each time.
        Assert.Same(arguments.ChildNodes[0], children[1].AsNode());
        Assert.Same(children[1].AsNode(), arguments.ChildNodesAndTokens()[1].AsNode());
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = children[5]; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = children[-1]; });
    }

    [Fact]
    public void ANodeOrTokenThatIsNeitherHasNoKindAndNoText()
    {
        var neither = default(SyntaxNodeOrToken);
        Assert.False(neither.IsNode);
        Assert.False(neither.IsToken);
        Assert.Equal(SyntaxKind.None, neither.Kind);
        Assert.Null(neither.Parent);
        Assert.Null(neither.AsNode());
        Assert.Equal(0, neither.Position);
        Assert.Equal(default, neither.Span);
        Assert.Equal(default, neither.FullSpan);
        Assert.Equal("", neither.ToFullString());
        Assert.Equal("", neither.ToString());
    }

    [Fact]
    public void ANodeAndATokenAreBothChildren()
    {
        var tokens = HandBuilt.Tokens("(a)");
        var arguments = HandBuilt.Over("(a)", new Green.ArgumentListSyntax(
            tokens[0], new GreenSeparatedList([HandBuilt.Name(tokens[1])]), tokens[2]));

        SyntaxNodeOrToken node = arguments.ChildNodes[0];
        SyntaxNodeOrToken token = arguments.ChildTokens[0];
        Assert.Equal(SyntaxKind.IdentifierName, node.Kind);
        Assert.Equal(arguments.ChildNodes[0].FullSpan, node.FullSpan);
        Assert.Equal(SyntaxKind.OpenParen, token.Kind);
        Assert.Equal("(", token.ToFullString());
    }

    [Fact]
    public void ChildrenMatchListAndSlicePatterns()
    {
        var tokens = HandBuilt.Tokens("(a)");
        var arguments = HandBuilt.Over("(a)", new Green.ArgumentListSyntax(
            tokens[0], new GreenSeparatedList([HandBuilt.Name(tokens[1])]), tokens[2]));

        var children = arguments.ChildNodesAndTokens();
        Assert.True(children is [{ Kind: SyntaxKind.OpenParen }, _, { Kind: SyntaxKind.CloseParen }]);
        var rest = children is [_, .. var tail] ? tail : [];
        Assert.Equal([SyntaxKind.IdentifierName, SyntaxKind.CloseParen], rest.Select(child => child.Kind));
        Assert.Equal(2, children.Slice(1, 2).Length);
    }

    /// <summary>
    /// Every node of a parsed file gives its children in source order, each starting where the
    /// one before it ends, and together they are the node's whole text.
    /// </summary>
    [Fact]
    public void ChildrenCoverTheNodeInSourceOrder()
    {
        var tree = SyntaxTree.Parse("test.nt65", """
            .module test

            .proc main: std {    ; the entry point
                lda #$10
                sta $2000,x
                jsr other!(1, 2 + 3)
            }

            .data table {
                .byte 1, 2, 3
            }
            """);

        foreach (var node in tree.Root.DescendantNodes())
        {
            var text = new StringBuilder();
            var position = node.Position;
            foreach (var child in node.ChildNodesAndTokens())
            {
                Assert.Equal(position, child.Position);
                text.Append(child.ToFullString());
                position += child.FullSpan.Length;
            }
            Assert.Equal(node.FullSpan.End, position);
            Assert.Equal(node.ToFullString(), text.ToString());
        }
    }
}
