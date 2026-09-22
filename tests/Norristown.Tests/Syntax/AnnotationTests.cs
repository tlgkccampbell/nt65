using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Annotations: a tag put on a node or a token that survives a rewrite, so that the piece a fix
/// inserted can be found again in the tree that comes back. The hard part is the text boundary — a
/// rewrite that reaches a line writes text and parses the file again — and most of what is here
/// is about what crosses it and what does not.
/// </summary>
public sealed class AnnotationTests
{
    private const string Program = ".proc main {\n    lda #16 ; the mask\n    sta mask\n    rts\n}\n";

    /// <summary>An annotation is no part of what a node says: same text, same width, same everything else.</summary>
    [Fact]
    public void AnAnnotationChangesNothingAboutWhatANodeSays()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var tag = new SyntaxAnnotation("probe", "the mask");

        var tagged = number.WithAdditionalAnnotations(tag);
        Assert.NotSame(number, tagged);
        Assert.Equal(number.ToFullString(), tagged.ToFullString());
        Assert.Equal(number.Kind, tagged.Kind);
        Assert.Equal(number.FullSpan.Length, tagged.FullSpan.Length);
        Assert.Equal(number.ContainsDiagnostics, tagged.ContainsDiagnostics);

        // It belongs to no file until a rewrite puts it into one, as an `Update` does.
        Assert.Null(tagged.Parent);

        // It is itself, and is found by being itself: what it says is for the reader.
        Assert.True(tagged.HasAnnotation(tag));
        Assert.False(tagged.HasAnnotation(new SyntaxAnnotation("probe", "the mask")));
        Assert.True(tagged.HasAnnotations("probe"));
        Assert.Equal([tag], tagged.GetAnnotations("probe"));
        Assert.Equal("probe: the mask", tag.ToString());

        // The node it came from is untouched, and so is the file.
        Assert.False(number.ContainsAnnotations);
        Assert.False(tree.Root.ContainsAnnotations);
    }

    /// <summary>What is put on comes off again, by the annotation itself or by its kind.</summary>
    [Fact]
    public void AnAnnotationComesOffAgain()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var one = new SyntaxAnnotation("probe");
        var two = new SyntaxAnnotation("other", "data");

        var tagged = number.WithAdditionalAnnotations(one, two);
        Assert.True(tagged.HasAnnotation(one));
        Assert.True(tagged.HasAnnotation(two));

        // Putting on what is already there changes nothing, and so is the same node.
        Assert.Same(tagged, tagged.WithAdditionalAnnotations(one, two));

        var without = tagged.WithoutAnnotations(one);
        Assert.False(without.HasAnnotation(one));
        Assert.True(without.HasAnnotation(two));
        Assert.False(tagged.WithoutAnnotations("other").HasAnnotation(two));
        Assert.Same(tagged, tagged.WithoutAnnotations("nothing of this kind"));

        // A token answers the same way.
        var token = number.Token.WithAdditionalAnnotations(one);
        Assert.True(token.ContainsAnnotations);
        Assert.Equal([one], token.GetAnnotations("probe"));
        Assert.True(token.HasAnnotations("probe"));
        Assert.False(token.WithoutAnnotations(one).ContainsAnnotations);
        Assert.Equal(number.Token.Text, token.Text);
    }

    /// <summary>
    /// A rewrite of a piece rebuilds the nodes above it, and what they carry goes on to what they
    /// become: a tag on a node outlives a change to what is under it, and a tag on a token
    /// outlives that token being spelled another way.
    /// </summary>
    [Fact]
    public void RebuildingANodeKeepsWhatItCarries()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().First();
        var tag = new SyntaxAnnotation("probe");

        var tagged = (InstructionStatementSyntax)instruction.WithAdditionalAnnotations(tag);
        var rebuilt = tagged.WithMnemonic(SyntaxFactory.Mnemonic("ldx").WithTriviaFrom(tagged.Mnemonic));
        Assert.True(rebuilt.HasAnnotation(tag));
        Assert.Equal("    ldx #16 ; the mask", rebuilt.ToFullString());

        // The same through Update, and through a replacement inside the node.
        Assert.True(tagged.Update(tagged.Mnemonic, null).HasAnnotation(tag));
        var number = tagged.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        Assert.True(tagged.ReplaceNode(number, SyntaxFactory.NumberExpression(SyntaxFactory.Number("$10")))
            .HasAnnotation(tag));

        // A token keeps its tag when it is respelled, and when it is given other trivia.
        var token = instruction.Mnemonic.WithAdditionalAnnotations(tag);
        Assert.True(token.WithText("ldx").HasAnnotation(tag));
        Assert.True(token.WithLeadingTrivia(SyntaxFactory.Space).HasAnnotation(tag));
        Assert.True(token.WithTriviaFrom(instruction.Mnemonic).HasAnnotation(tag));
    }

    /// <summary>
    /// The text boundary: a rewrite that reaches a line writes the line's text back and parses the
    /// file again, and the annotation is put back on the node the reparse makes of that same text.
    /// </summary>
    [Fact]
    public void AnAnnotationCrossesTheReparse()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var tag = new SyntaxAnnotation("probe");

        var root = tree.Root.ReplaceNode(number, number.WithAdditionalAnnotations(tag));
        Assert.NotSame(tree, root.Tree);
        Assert.Equal(Program, root.ToFullString());

        // A new tree, a new node, and the tag on the node standing where the old one stood.
        var found = Assert.Single(root.GetAnnotatedNodes(tag));
        Assert.IsType<NumberExpressionSyntax>(found);
        Assert.Equal("16", found.GetText());
        Assert.Equal(number.FullSpan, found.FullSpan);
        Assert.Equal([found], root.GetAnnotatedNodes("probe"));
        Assert.Equal([found], root.GetAnnotatedNodesAndTokens(tag).Select(piece => piece.AsNode()));

        // Rolled up, so that finding it costs a walk of the subtrees that hold one.
        Assert.True(root.ContainsAnnotations);
        Assert.True(found.Ancestors().All(node => node.ContainsAnnotations));
        Assert.False(root.Tree.GetLine(3).ContainsAnnotations);
    }

    /// <summary>A token tagged and written into a file is found again as a token.</summary>
    [Fact]
    public void AnAnnotatedTokenCrossesTheReparse()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var name = tree.Root.DescendantTokens().Single(token => token.Text == "mask");
        var tag = new SyntaxAnnotation("rename");

        var root = tree.Root.ReplaceToken(name, SyntaxFactory.Identifier("flags")
            .WithTriviaFrom(name)
            .WithAdditionalAnnotations(tag));
        Assert.Equal(".proc main {\n    lda #16 ; the mask\n    sta flags\n    rts\n}\n", root.ToFullString());

        var found = Assert.Single(root.GetAnnotatedTokens(tag));
        Assert.Equal("flags", found.Text);
        Assert.Equal(SyntaxKind.Identifier, found.Kind);
        Assert.Equal([found], root.GetAnnotatedTokens("rename"));
        Assert.Empty(root.GetAnnotatedNodes(tag));
    }

    /// <summary>
    /// An edit elsewhere in the file leaves an annotated line alone, green node and all, so what
    /// it carries is still there. A line the edit reaches is parsed again, and its annotations go
    /// with the parse they were on — which is what Roslyn does too.
    /// </summary>
    [Fact]
    public void AnEditKeepsWhatTheLinesItLeavesAloneCarry()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var tag = new SyntaxAnnotation("probe");
        var annotated = tree.Root.ReplaceNode(number, number.WithAdditionalAnnotations(tag)).Tree;

        // A line typed on three lines down: the annotated line keeps its green node and its tag.
        var elsewhere = annotated.WithChange(new TextChange(annotated.GetPosition(3, 7), 0, "  ; back"));
        Assert.Equal("16", Assert.Single(elsewhere.Root.GetAnnotatedNodes(tag)).GetText());

        // The line itself typed over: it is read again, and what was on it is gone.
        var over = annotated.WithChange(new TextChange(annotated.GetPosition(1, 9), 2, "32"));
        Assert.Empty(over.Root.GetAnnotatedNodes(tag));
        Assert.False(over.Root.ContainsAnnotations);
    }

    /// <summary>
    /// What the reparse makes no matching piece of loses its annotations, and says nothing about
    /// it. A line is the plainest case of that: a rewrite writes a line back as text, so a tag on
    /// what is written <em>on</em> the line crosses and a tag on the line itself has nothing to
    /// land on.
    /// </summary>
    [Fact]
    public void AnAnnotationWithNothingToLandOnIsDropped()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var line = tree.GetLine(2);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var written = new SyntaxAnnotation("on what is written");
        var whole = new SyntaxAnnotation("on the line");

        var root = tree.Root.ReplaceNodes<SyntaxNode>(
            [line, number],
            (old, _) => ReferenceEquals(old, line)
                ? line.WithAdditionalAnnotations(whole)
                : number.WithAdditionalAnnotations(written));
        Assert.Equal(Program, root.ToFullString());
        Assert.Equal("16", Assert.Single(root.GetAnnotatedNodes(written)).GetText());
        Assert.Empty(root.GetAnnotatedNodes(whole));
        Assert.False(root.Tree.GetLine(2).ContainsAnnotations);
    }

    /// <summary>Normalizing throws the trivia away and keeps the tags, which are not trivia.</summary>
    [Fact]
    public void NormalizingKeepsWhatAPieceCarries()
    {
        var instruction = SyntaxFactory.InstructionStatement(
            SyntaxFactory.Mnemonic("lda"),
            SyntaxFactory.ImmediateOperand(
                SyntaxFactory.Token(SyntaxKind.Hash),
                SyntaxFactory.NumberExpression(SyntaxFactory.Number("0"))));
        var tag = new SyntaxAnnotation("probe");
        var number = instruction.DescendantNodes().OfType<NumberExpressionSyntax>().Single();

        var tagged = instruction.ReplaceNode(number, number.WithAdditionalAnnotations(tag));
        var normalized = tagged.NormalizeWhitespace();
        Assert.Equal("lda #0", normalized.ToFullString());
        Assert.Equal("0", Assert.Single(normalized.GetAnnotatedNodes(tag)).GetText());

        // And on a token, which the normalizer rewrites every one of.
        var token = tagged.DescendantTokens().Single(one => one.Kind == SyntaxKind.Mnemonic);
        var onToken = tagged.ReplaceToken(token, token.WithAdditionalAnnotations(tag));
        Assert.Equal("lda", Assert.Single(onToken.NormalizeWhitespace().GetAnnotatedTokens(tag)).Text);
    }

    /// <summary>
    /// What a statement could not take is part of its line, and a tag on it survives a rewrite of
    /// that line: the tagging itself, and a name written over beside it, each read the line again.
    /// </summary>
    [Fact]
    public void ATagOnWhatALineLeftOverSurvivesARewriteOfTheLine()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".module marker: sideways\n");
        var skipped = tree.Root.DescendantNodes().First(node => node.Kind == SyntaxKind.SkippedTokens);
        var tag = new SyntaxAnnotation("probe");

        var tagged = tree.Root.ReplaceNode(skipped, skipped.WithAdditionalAnnotations(tag));
        var name = tagged.DescendantTokens().First(token => token.Text == "marker");
        var renamed = tagged.ReplaceToken(name, SyntaxFactory.Identifier("other").WithTriviaFrom(name));

        Assert.Equal("sideways", Assert.Single(tagged.GetAnnotatedNodes(tag)).GetText().Trim());
        Assert.Equal("sideways", Assert.Single(renamed.GetAnnotatedNodes(tag)).GetText().Trim());
        Assert.Equal(".module other: sideways\n", renamed.ToFullString());
    }

    /// <summary>
    /// The lexer's cache shares a token between the places a file writes it, so an annotated one
    /// must never get into it: the cache is the lexer's alone, and a tag makes a token of its own.
    /// </summary>
    [Fact]
    public void AnAnnotatedTokenIsNotSharedThroughTheCache()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var mnemonic = tree.Root.DescendantTokens().First(token => token.Kind == SyntaxKind.Mnemonic);
        _ = mnemonic.WithAdditionalAnnotations(new SyntaxAnnotation("probe"));

        // The same text, lexed again on this very thread: nothing of what was tagged comes back.
        var again = SyntaxTree.Parse("main.nt65", Program);
        Assert.False(again.Root.ContainsAnnotations);
        Assert.All(again.Root.DescendantTokens(), token => Assert.False(token.ContainsAnnotations));
        Assert.All(again.Root.DescendantNodes(), node => Assert.False(node.ContainsAnnotations));
    }

    /// <summary>
    /// Over every source in the repository: a node tagged, an identity rewrite run over it, and a
    /// token written over elsewhere on its line and on another line. The tagged node is found again
    /// each time, the same kind saying the same thing, and the file reads as it did.
    /// </summary>
    [Fact]
    public void ATaggedNodeIsFoundAgainWhateverElseTheRewriteWrites()
    {
        // Every fourth source, which is enough to meet every kind of line and costs a fifth of a
        // second rather than a second.
        var sources = Repo.Sources().Where((_, index) => index % 4 == 0).ToList();
        var failures = Repo.CollectFailures(sources, path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            if (Chosen(tree) is not { } node)
                return [];

            var tag = new SyntaxAnnotation("sweep", node.Kind.ToString());
            var said = node.GetText();
            var root = tree.Root.ReplaceNode(node, node.WithAdditionalAnnotations(tag));
            var problems = new List<string>(Found(Repo.Named(path), "tagging it", root, tag, node.Kind, said));
            if (root.ToFullString() != tree.Text)
                problems.Add($"{Repo.Named(path)}: tagging a node rewrote the file");

            // A rewrite that writes nothing gives back the very root it was given, tags and all.
            var untouched = new Untouched().Visit(root);
            if (!ReferenceEquals(untouched, root))
                problems.Add($"{Repo.Named(path)}: a rewrite of nothing gave back another root");

            // A token written over somewhere else, on the tagged node's line and on another.
            foreach (var (what, written) in Elsewhere(root, node))
                problems.AddRange(Found(Repo.Named(path), what, written, tag, node.Kind, said));
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// The node to tag: one written inside a statement, picked from the file by a seed of its own
    /// so that the sweep meets a different kind in every source and the same one every run.
    /// </summary>
    private static SyntaxNode? Chosen(SyntaxTree tree)
    {
        var nodes = tree.Root.DescendantNodes()
            .Where(node => node is not (LineSyntax or BlockSyntax or FileSyntax) && node.Span.Length > 0)
            .ToList();
        return nodes.Count == 0 ? null : nodes[new Random(tree.Text.Length).Next(nodes.Count)];
    }

    /// <summary>
    /// The file with one name written over, on the tagged node's own line and on another line,
    /// which is what makes the reparse cover the line the tag is on and the lines around it. Only
    /// the name of an <see cref="IdentifierNameSyntax"/> is written over, because a word that is a
    /// name wherever it stands is one the rewrite cannot change the shape of the line with.
    /// </summary>
    private static IEnumerable<(string What, SyntaxNode Written)> Elsewhere(SyntaxNode root, SyntaxNode tagged)
    {
        var line = root.Tree.GetLineIndex(tagged.Position);
        var names = root.DescendantTokens()
            .Where(token => token is { Kind: SyntaxKind.Identifier, IsMissing: false, Parent: IdentifierNameSyntax }
                && !tagged.FullSpan.Contains(token.FullSpan))
            .ToList();
        foreach (var (what, same) in new[] { ("a name on its line", true), ("a name on another line", false) })
        {
            if (names.FirstOrDefault(token => (root.Tree.GetLineIndex(token.Position) == line) == same) is not
                { Parent: not null } name)
            {
                continue;
            }
            yield return (what, root.ReplaceToken(name, SyntaxFactory.Identifier(name.Text + "_x").WithTriviaFrom(name)));
        }
    }

    /// <summary>What is wrong with the one node <paramref name="tag"/> is to be found on, if anything.</summary>
    private static IEnumerable<string> Found(
        string named, string what, SyntaxNode root, SyntaxAnnotation tag, SyntaxKind kind, string said)
    {
        var found = root.GetAnnotatedNodes(tag).ToList();
        if (found.Count != 1)
            yield return $"{named}: after {what}, {found.Count} nodes carry the tag";
        else if (found[0].Kind != kind || found[0].GetText() != said)
            yield return $"{named}: after {what}, the tag is on a {found[0].Kind} saying {found[0].GetText()}, not a {kind} saying {said}";
    }

    /// <summary>A rewrite that overrides nothing, which is the one that must change nothing.</summary>
    private sealed class Untouched : SyntaxRewriter
    {
    }
}
