using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The syntax half of the analysis API, used as an analyzer or an editor feature would use it.
/// Each test is written the way a caller outside nt65 would write it: nothing here reaches for a
/// test helper, and nothing reaches inside the syntax layer.
/// </summary>
public sealed class AnalysisApiTests
{
    /// <summary>A file is parsed, and the tree knows the text it came from.</summary>
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

    /// <summary>A tree is made of three things, which are nodes, tokens and trivia.</summary>
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

        // Span covers the text. FullSpan also takes in the trivia around it.
        Assert.Equal(new TextSpan(11, 1), brace.Span);
        Assert.Equal(new TextSpan(11, 21), brace.FullSpan);
    }

    /// <summary>
    /// A required token absent from the source is still in its slot, as a missing token.
    /// </summary>
    [Fact]
    public void FixedSlotsAndMissingTokens()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main\n    rts\n}\n");
        var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

        // The brace is required, so it is never null. It is missing, so it has no text and no
        // width, and it sits where it would have been.
        Assert.True(proc.OpenBraceToken.IsMissing);
        Assert.Equal("", proc.OpenBraceToken.Text);
        Assert.Equal(new TextSpan(10, 0), proc.OpenBraceToken.Span);

        // The node's own span is its text, so a missing token never stretches it.
        Assert.Equal(new TextSpan(0, 10), proc.Span);
        Assert.Equal(".proc main", proc.ToFullString());

        // An optional child element absent from the source is null instead, which is a different
        // thing from a missing token.
        Assert.Null(proc.Signature);
    }

    /// <summary>A list gives its items and its separators, which an editor needs as much as the items.</summary>
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

    /// <summary>A name is a path of parts rather than one token.</summary>
    [Fact]
    public void Names()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda hw::vic::border\n    lda count\n}\n");
        var names = tree.Root.DescendantNodes().OfType<NameExpressionSyntax>().ToList();

        Assert.Equal(["hw", "vic", "border"], names[0].Names.Select(name => name.Text));
        Assert.Null(names[0].SimpleName);
        Assert.Equal("border", names[0].LastPart!.Name.Text);

        // A name of one part with no qualifier also has a SimpleName, for the common case.
        Assert.Equal("count", names[1].SimpleName!.Value.Text);
    }

    /// <summary>A caller finds a place in the tree, and steps from one token to the next.</summary>
    [Fact]
    public void Navigation()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    rts\n}\n");
        var caret = tree.GetPosition(1, 9);

        var token = tree.Root.FindToken(caret);
        Assert.Equal(SyntaxKind.NumberLiteral, token.Kind);
        Assert.Equal("1", token.Text);

        // Every token belongs to the node it is part of, and every node to the line it is on.
        Assert.IsType<NumberExpressionSyntax>(token.Parent);
        Assert.IsType<ProcDeclarationSyntax>(
            tree.Root.FindNode(new TextSpan(0, 5)).AncestorsAndSelf().OfType<StatementSyntax>().First());
        Assert.Equal("#", token.GetPreviousToken()!.Value.Text);
        Assert.Equal(SyntaxKind.EndOfLine, token.GetNextToken()!.Value.Kind);
        Assert.Equal("lda", tree.GetLine(1).GetFirstToken()!.Value.Text);
    }

    /// <summary>A visitor dispatches on what a node is. A walker goes on down the tree.</summary>
    [Fact]
    public void VisitorsAndWalkers()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    sta $d020\n    rts\n}\n");

        var counter = new Mnemonics();
        counter.Visit(tree.Root);

        Assert.Equal(["lda", "sta", "rts"], counter.Found);
    }

    /// <summary>The tree reports what is wrong, and where.</summary>
    [Fact]
    public void DiagnosticsOnTheTree()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda (1\n}\n");

        // The root reports the diagnostics of the whole file, and ContainsDiagnostics tells
        // whether there are any without walking the tree.
        Assert.True(tree.Root.ContainsDiagnostics);
        Assert.Equal(tree.Diagnostics, tree.Root.GetDiagnostics());

        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().Single();
        Assert.True(instruction.ContainsDiagnostics);
        var reported = Assert.Single(instruction.GetDiagnostics());
        Assert.Equal("expected `)`", reported.Message);
        Assert.Equal(new Span("main.nt65", 2, 11, 11), reported.Span);

        // The diagnostic is attached to the missing token that stands where `)` should be.
        var missing = instruction.DescendantTokens().Single(token => token.IsMissing);
        Assert.Equal(SyntaxKind.CloseParen, missing.Kind);
        Assert.Equal(reported, Assert.Single(missing.GetDiagnostics()));
    }

    /// <summary>
    /// A tree is changed by a fix and by a rename, each coded the way a consumer would code it.
    /// </summary>
    [Fact]
    public void ChangingATree()
    {
        const string source = ".proc main {\n    lda #16 ; the mask\n    sta mask\n}\n";
        var tree = SyntaxTree.Parse("main.nt65", source);

        // The fix changes every immediate in decimal to hex.
        var hex = new Hexadecimal().Visit(tree.Root)!;
        Assert.Equal(".proc main {\n    lda #$10 ; the mask\n    sta mask\n}\n", hex.ToFullString());

        // The rename replaces every occurrence of one identifier with another.
        var named = hex.DescendantTokens().Where(token => token is { Kind: SyntaxKind.Identifier, Text: "mask" });
        var renamed = hex.ReplaceTokens(named, (old, _) => SyntaxFactory.Identifier("flags").WithTriviaFrom(old));
        Assert.Equal(".proc main {\n    lda #$10 ; the mask\n    sta flags\n}\n", renamed.ToFullString());

        // A rewritten file is a new tree. The tree it came from is unchanged.
        Assert.NotSame(tree, renamed.Tree);
        Assert.Equal(source, tree.Text);

        // A rewrite that finds nothing to do gives back the very node it was given.
        Assert.Same(renamed, new Hexadecimal().Visit(renamed));
    }

    /// <summary>
    /// An annotation is put on what a fix inserts, and found again in the tree the rewrite
    /// returns. The annotation is the only reliable way to find where the inserted child element
    /// ended up.
    /// </summary>
    [Fact]
    public void AnnotatingWhatAFixInserts()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8 {\n    rts\n}\n");
        var widened = new SyntaxAnnotation("widened");

        var root = new Widen(widened).Visit(tree.Root)!;
        Assert.Equal(".proc main: a8, i8 {\n    rts\n}\n", root.ToFullString());

        // This is where `i8` ended up, which is where an editor would put the caret. Counting
        // characters could not reliably find that place, but the annotation does.
        Assert.Equal(new TextSpan(16, 2), root.GetAnnotatedNodes(widened).Single().Span);

        // The annotation does not change the node's text, and it matches by identity: a new
        // annotation of the same kind is a different annotation.
        var inserted = root.GetAnnotatedNodes("widened").Single();
        Assert.Equal("i8", inserted.GetText());
        Assert.True(inserted.HasAnnotation(widened));
        Assert.False(inserted.HasAnnotation(new SyntaxAnnotation("widened")));
        Assert.Equal([widened], inserted.GetAnnotations("widened"));
        Assert.True(inserted.HasAnnotations("widened"));
        Assert.False(inserted.WithoutAnnotations(widened).ContainsAnnotations);
        Assert.Empty(root.GetAnnotatedTokens(widened));
        Assert.Equal([inserted], root.GetAnnotatedNodesAndTokens(widened).Select(piece => piece.AsNode()));

        // An edit on another line leaves the annotated line, and so its annotation, untouched.
        // An edit to the annotated line itself reparses that line, and the annotation is lost.
        var elsewhere = root.Tree.WithChange(new TextChange(root.Tree.GetPosition(1, 7), 0, "  ; done"));
        Assert.Equal("i8", elsewhere.Root.GetAnnotatedNodes(widened).Single().GetText());
        Assert.Empty(root.Tree
            .WithChange(new TextChange(root.Tree.GetPosition(0, 12), 2, "a16"))
            .Root.GetAnnotatedNodes(widened));

        // A token annotated and put in by a rewrite is found the same way.
        var name = root.DescendantTokens().Single(token => token.Text == "main");
        var renamed = new SyntaxAnnotation("renamed");
        var replaced = root.ReplaceToken(name, SyntaxFactory.Identifier("start")
            .WithTriviaFrom(name)
            .WithAdditionalAnnotations(renamed));
        Assert.Equal("start", replaced.GetAnnotatedTokens(renamed).Single().Text);
    }

    /// <summary>
    /// Represents a fix that adds <c>i8</c> to a routine whose state list is only <c>a8</c>. The
    /// item it adds carries an annotation so that whoever ran the fix can find it.
    /// </summary>
    /// <param name="tag">The tag to put on the item the fix inserts.</param>
    private sealed class Widen(SyntaxAnnotation tag) : SyntaxRewriter
    {
        public override SyntaxNode? VisitStateList(StateListSyntax node)
        {
            if (node.Items is not [StateFlagItemSyntax { Name.Text: "a8" } a8])
                return node;

            // The trailing trivia of the old last item belongs after the list, so it moves to the
            // new last item, and the space before the routine's `{` is kept.
            var added = SyntaxFactory.StateFlagItem(
                SyntaxFactory.Identifier("i8").WithTrailingTrivia(a8.Name.TrailingTrivia));
            return node.WithItems(SyntaxFactory.SeparatedList(
            [
                (StateItemSyntax)a8.ReplaceToken(a8.Name, a8.Name.WithTrailingTrivia()),
                (StateItemSyntax)added.WithAdditionalAnnotations(tag),
            ]));
        }
    }

    /// <summary>Collects every mnemonic in a file, in source order.</summary>
    private sealed class Mnemonics : SyntaxWalker
    {
        public List<string> Found { get; } = [];

        public override void VisitInstructionStatement(InstructionStatementSyntax node)
        {
            Found.Add(node.Mnemonic.Text);
            base.VisitInstructionStatement(node);
        }
    }

    /// <summary>Rewrites every immediate operand in decimal as hexadecimal.</summary>
    private sealed class Hexadecimal : SyntaxRewriter
    {
        public override SyntaxNode? VisitNumberExpression(NumberExpressionSyntax node) =>
            node.Parent is ImmediateOperandSyntax && int.TryParse(node.Token.Text, out var value)
                ? node.WithToken(node.Token.WithText($"${value:x2}"))
                : node;
    }
}
