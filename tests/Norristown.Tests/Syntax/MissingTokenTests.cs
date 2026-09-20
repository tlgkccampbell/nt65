using Norristown.Syntax;
using Green = Norristown.Syntax.InternalSyntax;
using GreenCache = Norristown.Syntax.InternalSyntax.GreenCache;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;
using Lexer = Norristown.Syntax.InternalSyntax.Lexer;

namespace Norristown.Tests.Syntax;

public sealed class MissingTokenTests
{
    [Fact]
    public void AMissingTokenHasNoTextNoTriviaAndNoWidth()
    {
        var brace = GreenToken.Missing(SyntaxKind.OpenBrace);
        Assert.True(brace.IsMissing);
        Assert.Equal("", brace.Text);
        Assert.Equal("", brace.ToFullString());
        Assert.Equal(0, brace.FullWidth);
        Assert.Equal(0, brace.LeadingWidth);
        Assert.Empty(brace.LeadingTrivia);
        Assert.Empty(brace.TrailingTrivia);
        Assert.Null(brace.Error);
    }

    [Fact]
    public void EveryMissingTokenOfAKindIsTheSameNode()
    {
        var brace = GreenToken.Missing(SyntaxKind.OpenBrace);
        Assert.Same(brace, GreenToken.Missing(SyntaxKind.OpenBrace));
        Assert.NotSame(brace, GreenToken.Missing(SyntaxKind.Identifier));
        Assert.Equal(SyntaxKind.Identifier, GreenToken.Missing(SyntaxKind.Identifier).Kind);
    }

    [Fact]
    public void AMissingTokenIsNeverTheTokenTheSourceWrote()
    {
        var written = Lexer.LexLine("{").Tokens[0];
        Assert.False(written.IsMissing);
        Assert.NotSame(GreenToken.Missing(SyntaxKind.OpenBrace), written);

        // The cache shares tokens the lexer asks for, and a missing one is not among them,
        // however little text a written token has.
        var empty = GreenCache.Token(SyntaxKind.Identifier, "", [], [], null);
        Assert.False(empty.IsMissing);
        Assert.NotSame(GreenToken.Missing(SyntaxKind.Identifier), empty);

        // The line break of a file's last line has no text either, and is there all the same.
        var end = Lexer.LexLine("").Tokens[^1];
        Assert.False(end.IsMissing);
        Assert.Equal(0, end.FullWidth);
    }

    [Fact]
    public void AMissingTokenSitsWhereItBelongsAndIsEmptyThere()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc {\n}\n");
        var opener = Assert.IsType<LineSyntax>(Assert.IsType<BlockSyntax>(tree.Root.Members[0]).Opener);
        var proc = Assert.IsType<ProcDeclarationSyntax>(opener.Statement);

        var name = proc.Name;
        Assert.True(name.IsMissing);
        Assert.Equal("", name.Text);
        Assert.Equal(new TextSpan(6, 0), name.Span);
        Assert.Equal(new TextSpan(6, 0), name.FullSpan);
        Assert.Empty(name.LeadingTrivia);
        Assert.Empty(name.TrailingTrivia);

        // The token after it is where it would be without the missing one, and the node still
        // reads back as its line.
        Assert.False(proc.OpenBraceToken.IsMissing);
        Assert.Equal(new TextSpan(6, 1), proc.OpenBraceToken.Span);
        Assert.Equal(".proc {", proc.ToFullString());
        Assert.Equal(new TextSpan(0, 7), proc.FullSpan);
        Assert.Equal(new TextSpan(0, 7), proc.Span);
    }

    [Fact]
    public void AMissingTokenDoesNotStretchANodesSpan()
    {
        // The `:` of the label is missing, and where it belongs is past the comment, so a node
        // measured from its text must not reach that far.
        var tokens = HandBuilt.Tokens("loop  ; note");
        var label = (LabelSyntax)HandBuilt.Over("loop  ; note",
            new Green.LabelSyntax(tokens[0], GreenToken.Missing(SyntaxKind.Colon)));

        Assert.Equal("loop  ; note", label.ToFullString());
        Assert.Equal(new TextSpan(0, 12), label.FullSpan);
        Assert.Equal(new TextSpan(0, 4), label.Span);
        Assert.Equal(new TextSpan(12, 0), label.ChildTokens[1].Span);
    }

    [Fact]
    public void ANodeOfNothingButMissingTokensIsEmptyWhereItBelongs()
    {
        var label = HandBuilt.Over("loop:", new Green.LabelSyntax(
            GreenToken.Missing(SyntaxKind.Identifier), GreenToken.Missing(SyntaxKind.Colon)));
        Assert.Equal("", label.ToFullString());
        Assert.Equal(new TextSpan(0, 0), label.Span);
        Assert.Equal(new TextSpan(0, 0), label.FullSpan);
    }
}
