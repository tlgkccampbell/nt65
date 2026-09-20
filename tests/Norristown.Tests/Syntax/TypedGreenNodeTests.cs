using Norristown.Syntax;
using Green = Norristown.Syntax.InternalSyntax;
using GreenList = Norristown.Syntax.InternalSyntax.GreenList;
using GreenNode = Norristown.Syntax.InternalSyntax.GreenNode;
using GreenSeparatedList = Norristown.Syntax.InternalSyntax.GreenSeparatedList;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Green nodes with a slot per piece, built by hand: the shape the parser is being moved onto.
/// A red class reads its slots whenever its green node is one of these, so everything an
/// analyzer asks of a node — its pieces, its spans, its children, its text, its visitor — is
/// answered here from the slots alone.
/// </summary>
public sealed class TypedGreenNodeTests
{
    /// <summary>A required token nobody wrote stands in its slot, empty, where it belongs.</summary>
    [Fact]
    public void ARequiredTokenNotWrittenIsMissingInItsSlot()
    {
        const string text = ".proc {";
        var tokens = HandBuilt.Tokens(text);
        var green = new Green.ProcDeclarationSyntax(
            tokens[0], GreenToken.Missing(SyntaxKind.Identifier), null, tokens[1]);
        var proc = Assert.IsType<ProcDeclarationSyntax>(HandBuilt.Over(text, green));

        Assert.Equal(4, green.SlotCount);
        Assert.Null(green.GetSlot(2));
        Assert.Equal(".proc", proc.Keyword.Text);
        Assert.Equal(new TextSpan(0, 5), proc.Keyword.Span);
        Assert.True(proc.Name.IsMissing);
        Assert.Equal("", proc.Name.Text);
        Assert.Equal(new TextSpan(6, 0), proc.Name.Span);
        Assert.Null(proc.Signature);
        Assert.Equal("{", proc.OpenBraceToken.Text);
        Assert.Equal(new TextSpan(6, 1), proc.OpenBraceToken.Span);

        // The missing token takes up nothing, so the node still reads back as its line.
        Assert.Equal(text, proc.ToFullString());
        Assert.Equal(new TextSpan(0, 7), proc.Span);

        // A slot holding nothing shows no child; the missing token is a child like any other.
        Assert.Equal([SyntaxKind.Directive, SyntaxKind.Identifier, SyntaxKind.OpenBrace],
            proc.ChildNodesAndTokens().Select(child => child.Kind));
        Assert.Empty(proc.ChildNodes);
        Assert.Equal("VisitProcDeclaration", proc.Accept(new Method()));
    }

    /// <summary>An optional node slot reads as null when it is empty and as its node when it is not.</summary>
    [Fact]
    public void AnOptionalNodeSlotReadsAsNullOrAsItsNode()
    {
        const string text = ".proc p: a8 {";
        var tokens = HandBuilt.Tokens(text);
        var signature = new Green.ProcSignatureSyntax(
            tokens[2],
            new Green.StateListSyntax(new GreenSeparatedList([new Green.StateFlagItemSyntax(tokens[3], null)])),
            null,
            null);
        var green = new Green.ProcDeclarationSyntax(tokens[0], tokens[1], signature, tokens[4]);
        var proc = Assert.IsType<ProcDeclarationSyntax>(HandBuilt.Over(text, green));

        Assert.Equal("p", proc.Name.Text);
        Assert.Equal(": a8", proc.Signature!.GetText());
        Assert.Equal(new TextSpan(7, 4), proc.Signature!.Span);
        Assert.Null(proc.Signature!.ArrowToken);
        Assert.Null(proc.Signature!.Exit);
        Assert.Equal(text, proc.ToFullString());

        // The signature is the node's one child, and the state items hang from the state list.
        Assert.Equal([proc.Signature], proc.ChildNodes);
        var items = proc.Signature!.Entry.Items;
        Assert.Equal(["a8"], items.Select(item => item.GetText()));
        Assert.Equal(1, HandBuilt.SeparatedList<StateItemSyntax>(proc.Signature!.Entry, 0).Count);
        Assert.Same(proc.Signature!.Entry, items[0].Parent);
    }

    /// <summary>A separated list is one slot, and reads back as its items and their commas.</summary>
    [Fact]
    public void ASeparatedListSlotReadsItsItemsAndSeparators()
    {
        const string text = ".export a, b";
        var tokens = HandBuilt.Tokens(text);
        var green = new Green.ExportDirectiveSyntax(
            tokens[0], new GreenSeparatedList([Item(tokens[1]), tokens[2], Item(tokens[3])]));
        var export = Assert.IsType<ExportDirectiveSyntax>(HandBuilt.Over(text, green));

        var items = HandBuilt.SeparatedList<ExportItemSyntax>(export, 1);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items.SeparatorCount);
        Assert.Equal(["a", "b"], items.Select(item => item.GetText()));
        Assert.Equal(",", items.GetSeparator(0).Text);
        Assert.Equal(new TextSpan(9, 1), items.GetSeparator(0).Span);

        // The node holding the list shows the items, not the list, and is their parent.
        Assert.Equal(["a", "b"], export.Items.Select(item => item.GetText()));
        Assert.Equal([SyntaxKind.Directive, SyntaxKind.ExportItem, SyntaxKind.Comma, SyntaxKind.ExportItem],
            export.ChildNodesAndTokens().Select(child => child.Kind));
        Assert.Same(export, items[0].Parent);
        Assert.Same(export, items.GetSeparator(0).Parent);
        Assert.Equal(text, export.ToFullString());
        Assert.Equal(new TextSpan(0, 12), export.Span);
        Assert.Equal("VisitExportDirective", export.Accept(new Method()));

        static GreenNode Item(GreenToken name) =>
            new Green.ExportItemSyntax(
                new Green.NameExpressionSyntax(
                    null, new GreenSeparatedList([new Green.IdentifierNameSyntax(name, null)])),
                null, null, null, null);
    }

    /// <summary>A token list is one slot too, and its tokens are the node's own children.</summary>
    [Fact]
    public void ATokenListSlotReadsItsTokens()
    {
        const string text = "nop lda";
        var tokens = HandBuilt.Tokens(text);
        var green = new Green.SkippedTokensSyntax(new GreenList([tokens[0], tokens[1]]));
        var skipped = Assert.IsType<SkippedTokensSyntax>(HandBuilt.Over(text, green));

        var list = HandBuilt.TokenList(skipped, 0);
        Assert.Equal(2, list.Count);
        Assert.Equal(["nop", "lda"], list.Select(token => token.Text));
        Assert.Equal(new TextSpan(4, 3), list[1].Span);
        Assert.Equal(["nop", "lda"], skipped.ChildTokens.Select(token => token.Text));
        Assert.Empty(skipped.ChildNodes);
        Assert.Equal(text, skipped.ToFullString());
        Assert.Equal("VisitSkippedTokens", skipped.Accept(new Method()));
    }

    /// <summary>An empty list slot is a slot with nothing in it, and reads as no items.</summary>
    [Fact]
    public void AnEmptyListSlotReadsAsNoItems()
    {
        var tokens = HandBuilt.Tokens(".export");
        var export = Assert.IsType<ExportDirectiveSyntax>(
            HandBuilt.Over(".export", new Green.ExportDirectiveSyntax(tokens[0], null)));

        Assert.Empty(export.Items);
        Assert.Equal(0, HandBuilt.SeparatedList<ExportItemSyntax>(export, 1).Count);
        Assert.Equal([SyntaxKind.Directive], export.ChildNodesAndTokens().Select(child => child.Kind));
        Assert.Equal(".export", export.ToFullString());
    }

    /// <summary>A node standing where a required one belongs says so, as a missing token does.</summary>
    [Fact]
    public void AnErrorExpressionIsMissing()
    {
        Assert.True(new Green.ErrorExpressionSyntax(null).IsMissing);
        Assert.False(new Green.BlankLineSyntax().IsMissing);
        Assert.True(GreenToken.Missing(SyntaxKind.OpenBrace).IsMissing);
    }

    /// <summary>The widths of the slots add up to the node's, missing tokens counting nothing.</summary>
    [Fact]
    public void ANodesWidthIsTheWidthOfItsSlots()
    {
        var tokens = HandBuilt.Tokens(".scope name {");
        var written = new Green.ScopeDeclarationSyntax(tokens[0], tokens[1], tokens[2]);
        Assert.Equal(".scope name {".Length, written.FullWidth);

        var unwritten = new Green.ScopeDeclarationSyntax(tokens[0], null, GreenToken.Missing(SyntaxKind.OpenBrace));
        Assert.Equal(".scope ".Length, unwritten.FullWidth);
        Assert.Equal(".scope ", unwritten.ToFullString());
    }

    /// <summary>A walk of a typed node reaches the items of its lists and no list node.</summary>
    [Fact]
    public void AWalkNeverMeetsTheNodeOverAList()
    {
        const string text = ".export a, b";
        var tokens = HandBuilt.Tokens(text);
        var export = HandBuilt.Over(text, new Green.ExportDirectiveSyntax(
            tokens[0],
            new GreenSeparatedList(
            [
                new Green.ExportItemSyntax(Name(tokens[1]), null, null, null, null),
                tokens[2],
                new Green.ExportItemSyntax(Name(tokens[3]), null, null, null, null),
            ])));

        Assert.Equal(
            [
                SyntaxKind.ExportItem, SyntaxKind.NameExpression, SyntaxKind.IdentifierName,
                SyntaxKind.ExportItem, SyntaxKind.NameExpression, SyntaxKind.IdentifierName,
            ],
            export.DescendantNodes().Select(node => node.Kind));

        static Green.NameExpressionSyntax Name(GreenToken name) =>
            new(null, new GreenSeparatedList([new Green.IdentifierNameSyntax(name, null)]));
    }

    /// <summary>The name of the visitor method a node is handed to.</summary>
    private sealed class Method : SyntaxVisitor<string>
    {
        public override string DefaultVisit(SyntaxNode node) => nameof(DefaultVisit);

        public override string VisitProcDeclaration(ProcDeclarationSyntax node) => nameof(VisitProcDeclaration);

        public override string VisitExportDirective(ExportDirectiveSyntax node) => nameof(VisitExportDirective);

        public override string VisitSkippedTokens(SkippedTokensSyntax node) => nameof(VisitSkippedTokens);
    }
}
