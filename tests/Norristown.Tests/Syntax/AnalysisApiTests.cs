using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The samples in <c>docs/ANALYSIS-API.md</c>, one test per section of it, so that the guide
/// cannot say something the API does not do. Each one is written the way a reader would write it:
/// nothing here reaches for a test helper, and nothing reaches inside the syntax layer.
/// </summary>
public sealed class AnalysisApiTests
{
    /// <summary>Parsing a file, and what a tree knows about the text it came from.</summary>
    [Fact]
    public void ParsingATree()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8, i8 {\n    lda #1\n    rts\n}\n");

        Assert.Equal(5, tree.LineCount);
        Assert.Equal(".proc main: a8, i8 {\n    lda #1\n    rts\n}\n", tree.Root.ToFullString());
        Assert.Equal(1, tree.GetLineIndex(tree.GetPosition(1, 4)));

        // An edit gives a new tree, keeping the green nodes of every line it does not touch.
        var edited = tree.WithChange(new TextChange(tree.GetPosition(1, 9), 1, "2"));
        Assert.Equal(".proc main: a8, i8 {\n    lda #2\n    rts\n}\n", edited.Text);
    }

    /// <summary>Nodes, tokens and trivia: the three things a tree is made of.</summary>
    [Fact]
    public void NodesTokensAndTrivia()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {   ; the entry point\n    rts\n}\n");
        var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

        Assert.Equal(SyntaxKind.ProcDeclaration, proc.Kind);
        Assert.Equal("main", proc.Name.Text);
        Assert.Equal("{", proc.OpenBraceToken.Text);
        Assert.Equal(".proc main {", proc.GetText());

        // A comment belongs to the token before it, and the indentation of a line to its first.
        var brace = proc.OpenBraceToken;
        Assert.Equal(
            [SyntaxKind.WhitespaceTrivia, SyntaxKind.CommentTrivia],
            brace.TrailingTrivia.Select(trivia => trivia.Kind));
        Assert.Equal("; the entry point", brace.TrailingTrivia[1].Text);
        Assert.Equal("    ", tree.GetLine(1).Tokens[0].LeadingTrivia.Single().Text);

        // Span is the text; FullSpan takes in the trivia around it.
        Assert.Equal(new TextSpan(11, 1), brace.Span);
        Assert.Equal(new TextSpan(11, 21), brace.FullSpan);
    }

    /// <summary>A piece the source did not write, which is in its slot all the same.</summary>
    [Fact]
    public void FixedSlotsAndMissingTokens()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main\n    rts\n}\n");
        var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

        // Required, so never null; missing, so no text and no width, placed where it belongs.
        Assert.True(proc.OpenBraceToken.IsMissing);
        Assert.Equal("", proc.OpenBraceToken.Text);
        Assert.Equal(new TextSpan(10, 0), proc.OpenBraceToken.Span);

        // The node's own span is its text, so a missing token never stretches it.
        Assert.Equal(new TextSpan(0, 10), proc.Span);
        Assert.Equal(".proc main", proc.ToFullString());

        // Nullable means the source wrote no signature at all, which is a different thing.
        Assert.Null(proc.Signature);
    }

    /// <summary>Lists, and the separators an editor needs as much as the items.</summary>
    [Fact]
    public void ListsAndSeparators()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8, i8, native {\n    rts\n}\n");
        var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

        var items = proc.Signature!.Entry.Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.SeparatorCount);
        Assert.Equal(["a8", "i8", "native"], items.Select(item => item.GetText()));
        Assert.Equal([",", ","], items.GetSeparators().Select(comma => comma.Text));
        Assert.Equal(
            ["a8", ",", "i8", ",", "native"],
            items.GetWithSeparators().Select(piece => piece.GetText()));

        // A list with no items is the empty list, never null.
        var macro = SyntaxTree.Parse("main.nt65", ".macro m() {\n    nop\n}\n")
            .Root.DescendantNodes().OfType<MacroDeclarationSyntax>().Single();
        Assert.Empty(macro.Parameters!.Parameters);
        Assert.Equal(0, macro.Parameters!.Parameters.SeparatorCount);
    }

    /// <summary>A name, which is a path of parts rather than one token.</summary>
    [Fact]
    public void Names()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda hw::vic::border\n    lda count\n}\n");
        var names = tree.Root.DescendantNodes().OfType<NameExpressionSyntax>().ToList();

        Assert.Equal(["hw", "vic", "border"], names[0].Names.Select(name => name.Text));
        Assert.Null(names[0].SimpleName);
        Assert.Equal("border", names[0].LastPart!.Name.Text);

        // One part and nothing before it is one name, which is the common case.
        Assert.Equal("count", names[1].SimpleName!.Value.Text);
    }

    /// <summary>Finding a place in the tree, and stepping from one token to the next.</summary>
    [Fact]
    public void Navigation()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    rts\n}\n");
        var caret = tree.GetPosition(1, 9);

        var token = tree.Root.FindToken(caret);
        Assert.Equal(SyntaxKind.NumberLiteral, token.Kind);
        Assert.Equal("1", token.Text);

        // Every token belongs to the node it is a piece of, and every node to the line it is on.
        Assert.IsType<NumberExpressionSyntax>(token.Parent);
        Assert.IsType<ProcDeclarationSyntax>(
            tree.Root.FindNode(new TextSpan(0, 5)).AncestorsAndSelf().OfType<StatementSyntax>().First());
        Assert.Equal("#", token.GetPreviousToken()!.Value.Text);
        Assert.Equal(SyntaxKind.EndOfLine, token.GetNextToken()!.Value.Kind);
        Assert.Equal("lda", tree.GetLine(1).GetFirstToken()!.Value.Text);
    }

    /// <summary>A visitor dispatches on what a node is; a walker goes on down the tree.</summary>
    [Fact]
    public void VisitorsAndWalkers()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    sta $d020\n    rts\n}\n");

        var counter = new Mnemonics();
        counter.Visit(tree.Root);

        Assert.Equal(["lda", "sta", "rts"], counter.Found);
    }

    /// <summary>What the tree says is wrong, and where.</summary>
    [Fact]
    public void DiagnosticsOnTheTree()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda (1\n}\n");

        // The root answers for the file, and says whether there is anything to answer without
        // walking anything.
        Assert.True(tree.Root.ContainsDiagnostics);
        Assert.Equal(tree.Diagnostics, tree.Root.GetDiagnostics());

        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().Single();
        Assert.True(instruction.ContainsDiagnostics);
        var reported = Assert.Single(instruction.GetDiagnostics());
        Assert.Equal("expected `)`", reported.Message);
        Assert.Equal(new Span("main.nt65", 2, 11, 11), reported.Span);

        // It belongs to the token standing where the `)` was not written.
        var missing = instruction.DescendantTokens().Single(token => token.IsMissing);
        Assert.Equal(SyntaxKind.CloseParen, missing.Kind);
        Assert.Equal(reported, Assert.Single(missing.GetDiagnostics()));
    }

    /// <summary>Changing a tree: a fix and a rename, written the way a consumer writes them.</summary>
    [Fact]
    public void ChangingATree()
    {
        const string written = ".proc main {\n    lda #16 ; the mask\n    sta mask\n}\n";
        var tree = SyntaxTree.Parse("main.nt65", written);

        // A fix: every immediate written in decimal is written in hex instead.
        var hex = new Hexadecimal().Visit(tree.Root)!;
        Assert.Equal(".proc main {\n    lda #$10 ; the mask\n    sta mask\n}\n", hex.ToFullString());

        // A rename: one name said another way, wherever it is written.
        var named = hex.DescendantTokens().Where(token => token is { Kind: SyntaxKind.Identifier, Text: "mask" });
        var renamed = hex.ReplaceTokens(named, (old, _) => SyntaxFactory.Identifier("flags").WithTriviaFrom(old));
        Assert.Equal(".proc main {\n    lda #$10 ; the mask\n    sta flags\n}\n", renamed.ToFullString());

        // A rewritten file is a new tree; the one it came from is what it was.
        Assert.NotSame(tree, renamed.Tree);
        Assert.Equal(written, tree.Text);

        // A rewrite that finds nothing to do gives back the very node it was given.
        Assert.Same(renamed, new Hexadecimal().Visit(renamed));
    }

    /// <summary>Every mnemonic a file writes, in source order.</summary>
    private sealed class Mnemonics : SyntaxWalker
    {
        public List<string> Found { get; } = [];

        public override void VisitInstructionStatement(InstructionStatementSyntax node)
        {
            Found.Add(node.Mnemonic.Text);
            base.VisitInstructionStatement(node);
        }
    }

    /// <summary>Every immediate operand written in decimal, written in hexadecimal instead.</summary>
    private sealed class Hexadecimal : SyntaxRewriter
    {
        public override SyntaxNode? VisitNumberExpression(NumberExpressionSyntax node) =>
            node.Parent is ImmediateOperandSyntax && int.TryParse(node.Token.Text, out var value)
                ? node.WithToken(node.Token.WithText($"${value:x2}"))
                : node;
    }
}
