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
        Assert.False(brace.ContainsDiagnostics);
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

        // The cache shares the tokens the lexer asks for, and never hands back a missing token,
        // even for a token that is present in the source but has no text.
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

    /// <summary>
    /// A <c>.use</c> that opens its braces has a place for the <c>}</c> that closes them, so the
    /// missing token stands there. A <c>.use</c> without braces has no place for one.
    /// </summary>
    [Fact]
    public void ABracedUseHoldsThePlaceForItsClosingBrace()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".use gfx::{clear\n.use gfx::fill\n");
        var braced = Assert.IsType<UseDirectiveSyntax>(tree.GetLine(0).Statement);

        Assert.NotNull(braced.CloseBraceToken);
        var close = braced.CloseBraceToken.Value;
        Assert.True(close.IsMissing);
        Assert.Equal(SyntaxKind.CloseBrace, close.Kind);
        Assert.Equal(new TextSpan(16, 0), close.Span);
        Assert.Equal(["expected `}`"], close.GetDiagnostics().Select(d => d.Message));
        Assert.Equal(new Span("test.nt65", 1, 17, 17), Assert.Single(close.GetDiagnostics()).Span);

        Assert.Null(Assert.IsType<UseDirectiveSyntax>(tree.GetLine(1).Statement).CloseBraceToken);
    }

    /// <summary>
    /// The diagnostic for a missing bracket names a fix that says what to insert and where,
    /// because only one thing can go there. A missing name is the programmer's to choose, so its
    /// diagnostic names no fix.
    /// </summary>
    [Fact]
    public void AMissingBracketNamesTheFixThatWritesIt()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc p   ; note\n}\n");
        var proc = Assert.IsType<ProcDeclarationSyntax>(tree.GetLine(0).Statement);

        var fix = Assert.Single(proc.OpenBraceToken.GetDiagnostics()).Fix;
        Assert.NotNull(fix);
        Assert.Equal(FixKind.MissingPiece, fix.Kind);
        Assert.Equal("{", fix.Text);

        var nameless = Assert.IsType<ProcDeclarationSyntax>(
            SyntaxTree.Parse("test.nt65", ".proc {\n}\n").GetLine(0).Statement);
        Assert.Null(Assert.Single(nameless.Name.GetDiagnostics()).Fix);
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
