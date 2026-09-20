using Norristown.Syntax;
using Green = Norristown.Syntax.InternalSyntax;
using GreenCache = Norristown.Syntax.InternalSyntax.GreenCache;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;
using Lexer = Norristown.Syntax.InternalSyntax.Lexer;

namespace Norristown.Tests.Syntax;

/// <summary>
/// A diagnostic belongs to the node or token it is about, and says where it is within that node
/// rather than where it is in the file, so an edit anywhere else leaves it alone.
/// </summary>
public sealed class TreeDiagnosticsTests
{
    [Fact]
    public void AMissingTokenCarriesWhatTheLineWantedThere()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc p   ; note\n");
        var proc = Assert.IsType<ProcDeclarationSyntax>(Line(tree, 0).Statement);

        var brace = proc.OpenBraceToken;
        Assert.True(brace.IsMissing);
        Assert.True(brace.ContainsDiagnostics);
        var reported = Assert.Single(brace.Green.Diagnostics);
        Assert.Equal("expected `{`, or `= address` for a routine with no body", reported.Message);

        // The token sits after the trivia that follows `p`, and the caret reaches back over it to
        // where the `{` belongs: the end of the last token the line really has.
        Assert.Equal(new TextSpan(16, 0), brace.FullSpan);
        Assert.Equal(-9, reported.Offset);
        Assert.Equal(0, reported.Width);
        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal(new Span("test.nt65", 1, 8, 8), diagnostic.Span);
        Assert.Equal(diagnostic, Assert.Single(brace.GetDiagnostics()));
    }

    /// <summary>
    /// A node holds what its children hold, all the way up, which is what lets a walk for
    /// diagnostics skip the subtrees that have none.
    /// </summary>
    [Fact]
    public void ContainsDiagnosticsRollsUpToTheParent()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc p {\nlda (1\n}\n");
        var instruction = Assert.IsType<InstructionStatementSyntax>(Line(tree, 1).Statement);
        Assert.True(instruction.ContainsDiagnostics);

        var operand = Assert.IsType<AbsoluteOperandSyntax>(instruction.Operand);
        Assert.True(operand.ContainsDiagnostics);
        Assert.False(instruction.Mnemonic.ContainsDiagnostics);
        Assert.Equal(["expected `)`"], instruction.GetDiagnostics().Select(d => d.Message));

        // The line above it holds nothing, and neither does anything under it.
        var proc = Line(tree, 0).Statement;
        Assert.False(proc.ContainsDiagnostics);
        Assert.Empty(proc.GetDiagnostics());
    }

    /// <summary>An unexpected token is reported on the tokens the statement could not take.</summary>
    [Fact]
    public void AnUnexpectedTokenIsReportedOnTheSkippedTokens()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".cpu 6502 nop\n");
        var line = Line(tree, 0);
        Assert.False(line.Statement.ContainsDiagnostics);

        var skipped = Assert.IsType<SkippedTokensSyntax>(line.SkippedTokens);
        Assert.True(skipped.ContainsDiagnostics);
        var diagnostic = Assert.Single(skipped.GetDiagnostics());
        Assert.Equal("unexpected `nop`", diagnostic.Message);
        Assert.Equal(new Span("test.nt65", 1, 11, 14), diagnostic.Span);
    }

    /// <summary>A lexical error stays on the token it covers, and the line says it holds one.</summary>
    [Fact]
    public void ALexicalErrorStaysOnItsToken()
    {
        var tree = SyntaxTree.Parse("test.nt65", "    lda $1G\n");
        var line = Line(tree, 0);
        Assert.True(line.ContainsDiagnostics);

        var number = line.ChildTokens[1];
        Assert.Equal("$1G", number.Text);
        Assert.True(number.ContainsDiagnostics);
        Assert.Equal(
            new Span("test.nt65", 1, 9, 12),
            Assert.Single(number.GetDiagnostics()).Span);
    }

    /// <summary>
    /// A token that reports something is one token's own, never the one the cache hands out for
    /// every place the same text is written.
    /// </summary>
    [Fact]
    public void ATokenWithADiagnosticIsNeverShared()
    {
        var errored = Lexer.LexLine("$1G").Tokens[0];
        Assert.True(errored.ContainsDiagnostics);
        Assert.NotSame(errored, Lexer.LexLine("$1G").Tokens[0]);
        Assert.NotSame(errored, GreenCache.Token(errored.Kind, "$1G", [], [], "invalid hexadecimal number `$1G`"));

        // The missing token of a kind is shared only while it says nothing.
        var quiet = GreenToken.Missing(SyntaxKind.OpenBrace);
        Assert.False(quiet.ContainsDiagnostics);
        Assert.Same(quiet, GreenToken.Missing(SyntaxKind.OpenBrace));
        var said = GreenToken.Missing(SyntaxKind.OpenBrace, new Green.GreenDiagnostic(0, 0, "expected `{`"));
        Assert.NotSame(quiet, said);
        Assert.True(said.ContainsDiagnostics);
        Assert.False(GreenToken.Missing(SyntaxKind.OpenBrace).ContainsDiagnostics);
    }

    /// <summary>
    /// The backtracking in an indirect operand throws away the nodes of the attempt, and the
    /// diagnostics go with them: <c>lda (a + b) * 2</c> is an expression, not a broken operand.
    /// </summary>
    [Fact]
    public void ABacktrackedAttemptLeavesNoDiagnostic()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc p {\nlda (a + b) * 2\n}\n");
        Assert.Empty(tree.Diagnostics);
        Assert.False(Line(tree, 1).Statement.ContainsDiagnostics);
    }

    private static LineSyntax Line(SyntaxTree tree, int index) =>
        tree.Root.DescendantNodes().OfType<LineSyntax>().ElementAt(index);
}
