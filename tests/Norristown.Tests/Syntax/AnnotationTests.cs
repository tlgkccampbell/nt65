using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks annotations. An annotation is a tag put on a node or a token that survives a rewrite,
/// so that the child element a fix inserted can be found again in the tree that comes back. The
/// hard part is the text boundary. A rewrite that changes a line writes that line out as text,
/// and the file is parsed again. Most of these tests are about which annotations survive that
/// reparse and which do not.
/// </summary>
public sealed class AnnotationTests
{
    private const string Program = ".proc main {\n    lda #16 ; the mask\n    sta mask\n    rts\n}\n";

    /// <summary>
    /// An annotation changes nothing else about a node. The text, the width and everything else
    /// stay the same.
    /// </summary>
    [Fact]
    public void AnAnnotationChangesNothingElseAboutANode()
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

        // Like a node made by `Update`, it belongs to no tree until a rewrite puts it into one.
        Assert.Null(tagged.Parent);

        // An annotation matches by identity, not by value: another with the same kind and data
        // is a different annotation. Its data is only there for whoever reads it.
        Assert.True(tagged.HasAnnotation(tag));
        Assert.False(tagged.HasAnnotation(new SyntaxAnnotation("probe", "the mask")));
        Assert.True(tagged.HasAnnotations("probe"));
        Assert.Equal([tag], tagged.GetAnnotations("probe"));
        Assert.Equal("probe: the mask", tag.ToString());

        // The node it came from is untouched, and so is the file.
        Assert.False(number.ContainsAnnotations);
        Assert.False(tree.Root.ContainsAnnotations);
    }

    /// <summary>An annotation can be removed again, by the annotation itself or by its kind.</summary>
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

        // Adding annotations the node already carries changes nothing, and returns the same node.
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
    /// Rewriting a child element rebuilds the nodes above it, and each rebuilt node keeps the
    /// annotations of the node it replaces. A tag on a node outlives a change to what is under it,
    /// and a tag on a token outlives that token being given other text.
    /// </summary>
    [Fact]
    public void RebuildingANodeKeepsItsAnnotations()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().First();
        var tag = new SyntaxAnnotation("probe");

        var tagged = (InstructionStatementSyntax)instruction.WithAdditionalAnnotations(tag);
        var rebuilt = tagged.WithMnemonic(SyntaxFactory.Mnemonic("ldx").WithTriviaFrom(tagged.Mnemonic));
        Assert.True(rebuilt.HasAnnotation(tag));
        Assert.Equal("    ldx #16 ; the mask", rebuilt.ToFullString());

        // The same holds through Update, and through a replacement inside the node.
        Assert.True(tagged.Update(tagged.Mnemonic, null).HasAnnotation(tag));
        var number = tagged.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        Assert.True(tagged.ReplaceNode(number, SyntaxFactory.NumberExpression(SyntaxFactory.Number("$10")))
            .HasAnnotation(tag));

        // A token keeps its tag when it is given other text, and when it is given other trivia.
        var token = instruction.Mnemonic.WithAdditionalAnnotations(tag);
        Assert.True(token.WithText("ldx").HasAnnotation(tag));
        Assert.True(token.WithLeadingTrivia(SyntaxFactory.Space).HasAnnotation(tag));
        Assert.True(token.WithTriviaFrom(instruction.Mnemonic).HasAnnotation(tag));
    }

    /// <summary>
    /// At the text boundary, a rewrite that reaches a line writes the line's text back and parses
    /// the file again. The annotation is put back on the node the reparse makes of that same text.
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

        // The result is a new tree and a new node, with the tag on the new node at the old node's
        // position.
        var found = Assert.Single(root.GetAnnotatedNodes(tag));
        Assert.IsType<NumberExpressionSyntax>(found);
        Assert.Equal("16", found.GetText());
        Assert.Equal(number.FullSpan, found.FullSpan);
        Assert.Equal([found], root.GetAnnotatedNodes("probe"));
        Assert.Equal([found], root.GetAnnotatedNodesAndTokens(tag).Select(piece => piece.AsNode()));

        // ContainsAnnotations is rolled up through the ancestors, so a search walks only the
        // subtrees that hold an annotation.
        Assert.True(root.ContainsAnnotations);
        Assert.True(found.Ancestors().All(node => node.ContainsAnnotations));
        Assert.False(root.Tree.GetLine(3).ContainsAnnotations);
    }

    /// <summary>A token that is tagged and inserted into a file is found again as a token.</summary>
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
    /// it carries is still there. A line the edit reaches is parsed again, and its annotations are
    /// discarded with the old parse, as Roslyn does too.
    /// </summary>
    [Fact]
    public void AnEditKeepsTheAnnotationsOfTheLinesItLeavesAlone()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var tag = new SyntaxAnnotation("probe");
        var annotated = tree.Root.ReplaceNode(number, number.WithAdditionalAnnotations(tag)).Tree;

        // Text typed on another line (line 3): the annotated line keeps its green node and its tag.
        var elsewhere = annotated.WithChange(new TextChange(annotated.GetPosition(3, 7), 0, "  ; back"));
        Assert.Equal("16", Assert.Single(elsewhere.Root.GetAnnotatedNodes(tag)).GetText());

        // Typing over the annotated line itself reparses it, and the annotation is lost.
        var over = annotated.WithChange(new TextChange(annotated.GetPosition(1, 9), 2, "32"));
        Assert.Empty(over.Root.GetAnnotatedNodes(tag));
        Assert.False(over.Root.ContainsAnnotations);
    }

    /// <summary>
    /// A child element for which the reparse makes no match loses its annotations, silently. A
    /// line is the plainest case of that. A rewrite writes a line back as text, so a tag on what is
    /// <em>on</em> the line survives, and a tag on the line node itself has nothing to land on.
    /// </summary>
    [Fact]
    public void AnAnnotationWithNothingToLandOnIsDropped()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var line = tree.GetLine(2);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();
        var inner = new SyntaxAnnotation("on what is written");
        var whole = new SyntaxAnnotation("on the line");

        var root = tree.Root.ReplaceNodes<SyntaxNode>(
            [line, number],
            (old, _) => ReferenceEquals(old, line)
                ? line.WithAdditionalAnnotations(whole)
                : number.WithAdditionalAnnotations(inner));
        Assert.Equal(Program, root.ToFullString());
        Assert.Equal("16", Assert.Single(root.GetAnnotatedNodes(inner)).GetText());
        Assert.Empty(root.GetAnnotatedNodes(whole));
        Assert.False(root.Tree.GetLine(2).ContainsAnnotations);
    }

    /// <summary>
    /// A token given text that the reparse reads as another kind of token has no match either, and
    /// loses its annotations just as silently. A number token given the text of a name is read
    /// back as an identifier, and the tag on the number is gone.
    /// </summary>
    [Fact]
    public void AnAnnotationOnATokenReadBackAsAnotherKindIsDropped()
    {
        var tree = SyntaxTree.Parse("main.nt65", Program);
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single().Token;
        var tag = new SyntaxAnnotation("probe");

        var root = tree.Root.ReplaceToken(number, number.WithText("mask").WithAdditionalAnnotations(tag));
        Assert.Equal(".proc main {\n    lda #mask ; the mask\n    sta mask\n    rts\n}\n", root.ToFullString());
        Assert.Equal(SyntaxKind.Identifier, root.FindToken(number.Span.Start).Kind);
        Assert.Empty(root.GetAnnotatedTokens(tag));
        Assert.False(root.ContainsAnnotations);
    }

    /// <summary>
    /// A rewrite with several changes parses the file again from the first to the last, so the
    /// lines in each gap between them are parsed again too. The tags in every gap come through,
    /// a tag inside a tagged node among them, even though each change moves the text after it.
    /// </summary>
    [Fact]
    public void TagsInEveryGapBetweenChangesCrossTheReparse()
    {
        var tree = SyntaxTree.Parse(
            "main.nt65", ".proc main {\n    lda #1\n    lda #2\n    lda #3\n    lda #4\n    lda #5\n    rts\n}\n");
        var numbers = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().ToList();
        var second = new SyntaxAnnotation("second");
        var fourth = new SyntaxAnnotation("fourth");
        var line = new SyntaxAnnotation("line");
        var instruction = numbers[3].Ancestors().OfType<InstructionStatementSyntax>().First();
        var tagged = tree.Root.ReplaceNodes<SyntaxNode>(
            [numbers[1], instruction],
            (old, _) => ReferenceEquals(old, instruction)
                ? instruction
                    .ReplaceNode(numbers[3], numbers[3].WithAdditionalAnnotations(fourth))
                    .WithAdditionalAnnotations(line)
                : old.WithAdditionalAnnotations(second));

        var odd = tagged.DescendantNodes().OfType<NumberExpressionSyntax>()
            .Where(number => number.GetText() is "1" or "3" or "5")
            .Select(number => number.Token);
        var root = tagged.ReplaceTokens(odd, (old, _) => old.WithText("$00" + old.Text));
        Assert.Equal(
            ".proc main {\n    lda #$001\n    lda #2\n    lda #$003\n    lda #4\n    lda #$005\n    rts\n}\n",
            root.ToFullString());
        Assert.Equal("2", Assert.Single(root.GetAnnotatedNodes(second)).GetText());
        Assert.Equal("4", Assert.Single(root.GetAnnotatedNodes(fourth)).GetText());
        Assert.Equal("lda #4", Assert.Single(root.GetAnnotatedNodes(line)).GetText());
    }

    /// <summary>Normalizing throws the trivia away and keeps the tags, which are not trivia.</summary>
    [Fact]
    public void NormalizingKeepsAnnotationsAndDropsTrivia()
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

        // The same holds for a tag on a token, even though the normalizer rewrites every token.
        var token = tagged.DescendantTokens().Single(one => one.Kind == SyntaxKind.Mnemonic);
        var onToken = tagged.ReplaceToken(token, token.WithAdditionalAnnotations(tag));
        Assert.Equal("lda", Assert.Single(onToken.NormalizeWhitespace().GetAnnotatedTokens(tag)).Text);
    }

    /// <summary>
    /// Tokens a statement could not take are skipped tokens that belong to its line, and a tag on
    /// them survives a rewrite of that line. Both the rewrite that adds the tag and a rename of a
    /// name beside it reparse the line.
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
    /// The lexer's cache shares one token instance among all the places it appears in a file, so
    /// an annotated token must never get into it. Adding a tag makes a new token, and the cache
    /// holds only tokens the lexer made.
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
    /// Over every source in the repository, a node is tagged, an identity rewrite is run over it,
    /// and a token is replaced elsewhere on its line and on another line. The tagged node is found
    /// again each time, with the same kind and the same text, and the file's text is unchanged.
    /// </summary>
    [Fact]
    public void ATaggedNodeIsFoundAgainWhateverElseTheRewriteWrites()
    {
        // Every fourth source is enough to meet every kind of line, and costs a fifth of a second
        // rather than a second.
        var sources = Repo.Sources().Where((_, index) => index % 4 == 0).ToList();
        var failures = Repo.CollectFailures(sources, path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            if (Chosen(tree) is not { } node)
                return [];

            var tag = new SyntaxAnnotation("sweep", node.Kind.ToString());
            var text = node.GetText();
            var root = tree.Root.ReplaceNode(node, node.WithAdditionalAnnotations(tag));
            var problems = new List<string>(Found(Repo.Named(path), "tagging it", root, tag, node.Kind, text));
            if (root.ToFullString() != tree.Text)
                problems.Add($"{Repo.Named(path)}: tagging a node rewrote the file");

            // A rewrite that changes nothing gives back the very root it was given, tags and all.
            var untouched = new Untouched().Visit(root);
            if (!ReferenceEquals(untouched, root))
                problems.Add($"{Repo.Named(path)}: a rewrite of nothing gave back another root");

            // A token is replaced somewhere else, on the tagged node's line and on another.
            foreach (var (what, replaced) in Elsewhere(root, node))
                problems.AddRange(Found(Repo.Named(path), what, replaced, tag, node.Kind, text));
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Returns the node to tag, which is a node inside a statement. It is picked at random with a
    /// seed taken from the file's length, so that the sweep meets different kinds across sources
    /// and the same node on every run.
    /// </summary>
    private static SyntaxNode? Chosen(SyntaxTree tree)
    {
        var nodes = tree.Root.DescendantNodes()
            .Where(node => node is not (LineSyntax or BlockSyntax or FileSyntax) && node.Span.Length > 0)
            .ToList();
        return nodes.Count == 0 ? null : nodes[new Random(tree.Text.Length).Next(nodes.Count)];
    }

    /// <summary>
    /// Returns the file with one name replaced, on the tagged node's own line and on another line,
    /// so that the reparse covers the line the tag is on and the lines around it. Only the name of
    /// an <see cref="IdentifierNameSyntax"/> is replaced, because renaming a word that is a name
    /// wherever it stands cannot change how the line parses.
    /// </summary>
    private static IEnumerable<(string What, SyntaxNode Replaced)> Elsewhere(SyntaxNode root, SyntaxNode tagged)
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

    /// <summary>
    /// Returns the problems, if any, with finding <paramref name="tag"/> on exactly one node, of
    /// kind <paramref name="kind"/> and with the text <paramref name="text"/>.
    /// </summary>
    private static IEnumerable<string> Found(
        string named, string what, SyntaxNode root, SyntaxAnnotation tag, SyntaxKind kind, string text)
    {
        var found = root.GetAnnotatedNodes(tag).ToList();
        if (found.Count != 1)
            yield return $"{named}: after {what}, {found.Count} nodes carry the tag";
        else if (found[0].Kind != kind || found[0].GetText() != text)
            yield return $"{named}: after {what}, the tag is on a {found[0].Kind} saying {found[0].GetText()}, not a {kind} saying {text}";
    }

    /// <summary>
    /// Represents a rewrite that overrides nothing, which is the one rewrite that must change
    /// nothing.
    /// </summary>
    private sealed class Untouched : SyntaxRewriter
    {
    }
}
