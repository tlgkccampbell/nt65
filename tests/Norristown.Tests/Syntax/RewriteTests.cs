using System.Globalization;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Rewriting a tree: <c>Update</c> and the <c>With</c>s, <see cref="SyntaxFactory"/>,
/// <see cref="SyntaxRewriter"/> and the replacements on a node. Two rules hold everything else
/// up: a rewrite that changes nothing gives back what it was given, the same objects and all,
/// and a rewrite of one piece leaves every other character of the file where it was. Both are
/// checked over every nt65 source in the repository.
/// </summary>
public sealed class RewriteTests
{
    /// <summary>
    /// Updating a slot with what it already holds is no change, so the same node comes back.
    /// </summary>
    [Fact]
    public void UpdateWithWhatIsAlreadyThereGivesBackTheSameNode()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n}\n");
        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().Single();

        Assert.Same(instruction, instruction.Update(instruction.Mnemonic, instruction.Operand));
        Assert.Same(instruction, instruction.WithMnemonic(instruction.Mnemonic));
        Assert.Same(instruction, instruction.WithOperand(instruction.Operand));

        // A token given the text it already has is the same token, so that changes nothing either.
        Assert.Same(instruction, instruction.WithMnemonic(instruction.Mnemonic.WithText("lda")));
    }

    /// <summary>
    /// A <c>With</c> gives a new node with no parent, in a tree of its own holding only its text,
    /// until a rewrite puts it into a file.
    /// </summary>
    [Fact]
    public void WithGivesANodeThatBelongsToNoFileYet()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n}\n");
        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().Single();

        // WithTriviaFrom copies the trivia around the replaced token, the line's indentation
        // among it, so the new token sits where the old one did.
        var built = instruction.WithMnemonic(SyntaxFactory.Mnemonic("ldx").WithTriviaFrom(instruction.Mnemonic));
        Assert.NotSame(instruction, built);
        Assert.Equal("    ldx #1", built.ToFullString());
        Assert.Null(built.Parent);
        Assert.Equal("    ldx #1", built.Tree.Text);

        // The file it came from is untouched: a tree is immutable.
        Assert.Equal(".proc main {\n    lda #1\n}\n", tree.Text);
    }

    /// <summary>A rewrite that overrides nothing gives back the root it was given.</summary>
    [Fact]
    public void ARewriteThatChangesNothingGivesBackTheSameRoot()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            return ReferenceEquals(new Untouched().Visit(tree.Root), tree.Root)
                ? []
                : [$"{Repo.Named(path)}: a rewrite of nothing gave back another root"];
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Every name in the file replaced by a new token with the same text: the tree is rebuilt
    /// from end to end and reads back as the file it came from, byte for byte.
    /// </summary>
    [Fact]
    public void RebuildingEveryNameWritesTheSameFile()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            var rewritten = new Rebuilt().Visit(tree.Root)!;
            return rewritten.ToFullString() == tree.Text
                ? []
                : [$"{Repo.Named(path)}: rebuilding every name did not write the file back"];
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Replacing the first number in every source that has one changes that span of the file
    /// and nothing else.
    /// </summary>
    [Fact]
    public void ReplacingOneNumberChangesOnlyItsSpan()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            if (tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().FirstOrDefault() is not { } number)
                return [];

            var span = number.Token.Span;
            var rewritten = tree.Root.ReplaceToken(number.Token, number.Token.WithText("$99"));
            var expected = string.Concat(tree.Text.AsSpan(0, span.Start), "$99", tree.Text.AsSpan(span.End));
            return rewritten.ToFullString() == expected
                ? []
                : [$"{Repo.Named(path)}: replacing {number.Token.Text} moved something else"];
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>A rewritten file is a new tree, and the tree it came from is what it was.</summary>
    [Fact]
    public void ARewrittenFileIsANewTree()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    rts\n}\n");
        var number = tree.Root.DescendantNodes().OfType<NumberExpressionSyntax>().Single();

        var root = tree.Root.ReplaceToken(number.Token, number.Token.WithText("2"));
        Assert.NotSame(tree, root.Tree);
        Assert.Equal(".proc main {\n    lda #2\n    rts\n}\n", root.Tree.Text);
        Assert.Equal(".proc main {\n    lda #1\n    rts\n}\n", tree.Text);

        // The result is a whole tree, with its own diagnostics, lines and blocks, not a fragment.
        Assert.Empty(root.Tree.Diagnostics);
        Assert.Equal(5, root.Tree.LineCount);
        Assert.IsType<BlockSyntax>(root.Tree.GetLine(1).Parent);
    }

    /// <summary>A node written over another, and a line taken out of the file it is on.</summary>
    [Fact]
    public void ANodeIsReplacedAndALineIsRemoved()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    nop\n    rts\n}\n");

        var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().First();
        var operand = instruction.Operand!;
        var replaced = tree.Root.ReplaceNode(operand, SyntaxFactory.AccumulatorOperand(
            SyntaxFactory.Token(SyntaxKind.Register, "a")));
        Assert.Equal(".proc main {\n    lda a\n    nop\n    rts\n}\n", replaced.ToFullString());

        var nop = tree.Root.DescendantNodes().OfType<LineSyntax>()
            .Single(line => line.Statement is InstructionStatementSyntax { Mnemonic.Text: "nop" });
        Assert.Equal(".proc main {\n    lda #1\n    rts\n}\n", tree.Root.RemoveNode(nop).ToFullString());
    }

    /// <summary>An item taken out of a separated list goes with the comma written after it.</summary>
    [Fact]
    public void AListItemIsRemovedWithItsSeparator()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8, i8, native {\n    rts\n}\n");
        var items = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single().Signature!.Entry.Items;

        Assert.Equal(".proc main: a8, native {\n    rts\n}\n", tree.Root.RemoveNode(items[1]).ToFullString());
        Assert.Equal(".proc main: a8, i8 {\n    rts\n}\n", tree.Root.RemoveNode(items[2]).ToFullString());
    }

    /// <summary>A node built out of bare tokens, and the spacing put on it afterwards.</summary>
    [Fact]
    public void ABuiltNodeIsWrittenWithTheSpacingItNeeds()
    {
        var instruction = SyntaxFactory.InstructionStatement(
            SyntaxFactory.Mnemonic("lda"),
            SyntaxFactory.ImmediateOperand(
                SyntaxFactory.Token(SyntaxKind.Hash),
                SyntaxFactory.NumberExpression(SyntaxFactory.Number("0"))));

        // The factory invents no whitespace, so what it builds is written tight.
        Assert.Equal("lda#0", instruction.ToFullString());
        Assert.Equal("lda #0", instruction.NormalizeWhitespace().ToFullString());

        var data = SyntaxFactory.DataValues(SyntaxFactory.SeparatedList(
            Enumerable.Range(0, 3).Select(power =>
                (SyntaxNode)SyntaxFactory.NumberExpression(
                    SyntaxFactory.Number((1 << power).ToString(CultureInfo.InvariantCulture))))));
        Assert.Equal("1, 2, 4", data.ToFullString());
    }

    /// <summary>
    /// A normalized file reads back as the tokens it was written with. The spacing is not the
    /// file's own — <see cref="Formatter"/> is what lays a line out — but nothing runs together
    /// and nothing is lost.
    /// </summary>
    [Fact]
    public void NormalizingAFileKeepsEveryTokenItHolds()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            var normalized = tree.Root.NormalizeWhitespace();
            var before = Written(tree.Root);
            var after = Written(normalized);
            if (before.SequenceEqual(after, StringComparer.Ordinal))
                return [];
            var at = before.Zip(after).Select((pair, i) => (pair, i)).FirstOrDefault(p => p.pair.First != p.pair.Second);
            return [$"{Repo.Named(path)}: token {at.i} reads {at.pair.Second} and was {at.pair.First}"];
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// The text of every token under a node, leaving out missing tokens and line breaks.
    /// </summary>
    private static List<string> Written(SyntaxNode node) =>
        [.. node.DescendantTokens()
            .Where(token => !token.IsMissing && token.Kind != SyntaxKind.EndOfLine)
            .Select(token => token.Text)];

    /// <summary>A rewrite that overrides nothing, which is the one that must change nothing.</summary>
    private sealed class Untouched : SyntaxRewriter
    {
    }

    /// <summary>Replaces every identifier with a new token of the same text and trivia.</summary>
    private sealed class Rebuilt : SyntaxRewriter
    {
        public override SyntaxToken VisitToken(SyntaxToken token) => token.Kind == SyntaxKind.Identifier
            ? SyntaxFactory.Identifier(token.Text).WithTriviaFrom(token)
            : token;
    }
}
