using System.Text;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks finding a place in the tree. That covers finding the token a caret is in and the node a
/// range names, and stepping from one token to the next. The sweep covers every source in the
/// repository, whole and with each line cut short, because an editor asks these questions of a
/// file nobody has finished typing.
/// </summary>
public sealed class NavigationTests
{
    /// <summary>
    /// The most problems each check reports for one variant. Beyond that, more reports would
    /// only repeat the same failure, and each check stops as soon as it has reported that many.
    /// </summary>
    private const int MaximumProblems = 5;

    /// <summary>
    /// Every source, whole and cut short, put through every way of finding a place in it. The
    /// checks share one parse of each variant, because parsing the variants costs more than
    /// running the checks does.
    /// </summary>
    [Fact]
    public void FindingAPositionWorksOnWholeAndBrokenLines()
    {
        // One variant of one source is the unit of work, rather than a whole source: a long file
        // costs more than a short one, so splitting them apart keeps every core busy to the end.
        var failures = Repo.CollectFailures(SourceVariant.All(), variant =>
        {
            var tree = SyntaxTree.Parse(variant.Path, variant.Text);
            IEnumerable<string>[] checks =
            [
                EveryPositionFindsTheTokenAtIt(tree),
                TheTokensOfTheRootAreTheFile(tree),
                SteppingFromATokenWalksTheFile(tree),
                EveryNodeIsFoundByItsSpanAndHeldByItsParent(tree),
            ];
            return checks.SelectMany(check => check.Take(MaximumProblems)).Select(problem => $"{variant.Where}: {problem}");
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// A caret in the whitespace before a token, or in the comment after one, finds the token the
    /// trivia sits beside, and the trivia itself is found by the same position.
    /// </summary>
    [Fact]
    public void ACaretInTriviaFindsTheTokenItStandsBeside()
    {
        var tree = SyntaxTree.Parse("test.nt65", "    lda #$10   ; load\n");
        Assert.Equal("lda", tree.Root.FindToken(0).Text);
        Assert.Equal("lda", tree.Root.FindToken(4).Text);
        Assert.Equal(SyntaxKind.WhitespaceTrivia, tree.Root.FindTrivia(2)?.Kind);
        Assert.Null(tree.Root.FindTrivia(4));

        // The whitespace and the comment after `$10` belong to it, so a caret in either is on it.
        var number = tree.Root.FindToken(9);
        Assert.Equal("$10", number.Text);
        Assert.Equal("$10", tree.Root.FindToken(13).Text);
        Assert.Equal("$10", tree.Root.FindToken(18).Text);
        Assert.Equal(SyntaxKind.CommentTrivia, tree.Root.FindTrivia(18)?.Kind);
        Assert.Equal("; load", tree.Root.FindTrivia(18)?.Text);
        Assert.Equal(new TextSpan(15, 6), tree.Root.FindTrivia(18)?.Span);
        Assert.Equal(number, tree.Root.FindTrivia(18)?.Token);

        // The line break is a token of its own, and the end of the file is past everything.
        Assert.Equal(SyntaxKind.EndOfLine, tree.Root.FindToken(21).Kind);
        Assert.Equal(SyntaxKind.EndOfLine, tree.Root.FindToken(tree.Text.Length).Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Root.FindToken(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Root.FindToken(tree.Text.Length + 1));
    }

    /// <summary>
    /// A line's children are its child elements, and its tokens hang from the nodes they are part
    /// of, so a walk of the file meets each of them once.
    /// </summary>
    [Fact]
    public void ALineShowsThePiecesItIsMadeOf()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".export .proc main {\n}\nlda #1 nop\n");
        var opener = ((BlockSyntax)tree.Root.Members[0]).Opener;
        Assert.Equal(
            [SyntaxKind.Directive, SyntaxKind.ProcDeclaration, SyntaxKind.EndOfLine],
            opener.ChildNodesAndTokens().Select(child => child.Kind));
        Assert.Equal([".export", ".proc", "main", "{", "\n"], opener.DescendantTokens().Select(token => token.Text));
        Assert.Equal([".export", ".proc", "main", "{", "\n"], opener.Tokens.Select(token => token.Text));
        Assert.Same(opener.Statement, tree.Root.FindToken(opener.Statement.Span.Start).Parent);
        Assert.Same(opener, tree.Root.FindToken(opener.Position).Parent);

        // Tokens the statement could not take are a separate child element of the line, after the
        // statement.
        var skipped = (LineSyntax)((SyntaxNode)tree.Root).ChildNodes[1];
        Assert.Equal(
            [SyntaxKind.InstructionStatement, SyntaxKind.SkippedTokens, SyntaxKind.EndOfLine],
            skipped.ChildNodesAndTokens().Select(child => child.Kind));
        Assert.Equal(["lda", "#", "1", "nop", "\n"], skipped.DescendantTokens().Select(token => token.Text));
        Assert.Equal(SyntaxKind.SkippedTokens, tree.Root.FindToken(skipped.Position + 7).Parent.Kind);
    }

    /// <summary>
    /// A node's first and last tokens are found both with and without the tokens that have no
    /// text. A missing token stands in the tree where a child element belongs and has no text.
    /// </summary>
    [Fact]
    public void TheFirstAndLastTokensSkipWhatHasNoText()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc main\n");
        var proc = ((LineSyntax)tree.Root.Members[0]).Statement;
        Assert.Equal(".proc", proc.GetFirstToken()?.Text);
        Assert.Equal("main", proc.GetLastToken()?.Text);
        Assert.Equal(".proc", proc.GetFirstToken(includeZeroWidth: true)?.Text);
        Assert.Equal(SyntaxKind.OpenBrace, proc.GetLastToken(includeZeroWidth: true)?.Kind);
        Assert.True(proc.GetLastToken(includeZeroWidth: true)?.IsMissing);

        // A file's last line is the empty one after its last break, and it has no text at all.
        var last = tree.GetLine(tree.LineCount - 1);
        Assert.Null(last.GetFirstToken());
        Assert.Equal(SyntaxKind.EndOfLine, last.GetLastToken(includeZeroWidth: true)?.Kind);
    }

    /// <summary>
    /// A node's ancestors are found innermost first, and so is the nearest one of a kind.
    /// </summary>
    [Fact]
    public void AncestorsAreTheNodesHoldingANode()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc main {\n    lda #$10\n}\n");
        var number = tree.Root.FindToken(tree.Text.IndexOf("$10", StringComparison.Ordinal)).Parent;
        Assert.Equal(
            [
                SyntaxKind.NumberExpression, SyntaxKind.ImmediateOperand, SyntaxKind.InstructionStatement,
                SyntaxKind.Line, SyntaxKind.Block, SyntaxKind.File,
            ],
            number.AncestorsAndSelf().Select(node => node.Kind));
        Assert.Equal(
            [
                SyntaxKind.ImmediateOperand, SyntaxKind.InstructionStatement,
                SyntaxKind.Line, SyntaxKind.Block, SyntaxKind.File,
            ],
            number.Ancestors().Select(node => node.Kind));
        Assert.Equal(1, number.FirstAncestorOrSelf<LineSyntax>()?.LineIndex);
        Assert.Same(tree.GetLine(1), number.FirstAncestorOrSelf<LineSyntax>());
    }

    /// <summary>Stepping from token to token crosses a line break and stops at the end of the file.</summary>
    [Fact]
    public void SteppingCrossesLinesAndStopsAtTheEnds()
    {
        var tree = SyntaxTree.Parse("test.nt65", "nop\nlda #1\n");
        var first = tree.GetLine(0).GetFirstToken()!.Value;
        Assert.Equal("nop", first.Text);
        Assert.Null(first.GetPreviousToken());
        Assert.Equal(SyntaxKind.EndOfLine, first.GetNextToken()?.Kind);
        Assert.Equal("lda", first.GetNextToken()?.GetNextToken()?.Text);
        Assert.Equal("nop", first.GetNextToken()?.GetNextToken()?.GetPreviousToken()?.GetPreviousToken()?.Text);

        // The file ends with the empty line after its last break, whose own break has no text.
        var end = tree.Root.GetLastToken(includeZeroWidth: true)!.Value;
        Assert.Equal(SyntaxKind.EndOfLine, end.Kind);
        Assert.Equal("", end.Text);
        Assert.Null(end.GetNextToken());
    }

    /// <summary>
    /// Every position of the file finds a token. It is the token whose text or trivia holds the
    /// position, never a missing one, and the same token the walk over the tree's tokens has at
    /// that place.
    /// </summary>
    private static IEnumerable<string> EveryPositionFindsTheTokenAtIt(SyntaxTree tree)
    {
        var tokens = tree.Root.DescendantTokens().ToList();

        // The walk over the tokens and the lookup by position read the same file, so they are
        // stepped through together rather than searched one against the other.
        var at = 0;
        for (var position = 0; position < tree.Text.Length; position++)
        {
            var found = tree.Root.FindToken(position);
            while (at < tokens.Count && tokens[at].FullSpan.End <= position)
                at++;
            if (!found.FullSpan.Contains(position))
                yield return $"{position} finds {Describe(found)}, which is not written there";
            else if (found.IsMissing)
                yield return $"{position} finds the missing {found.Kind}";
            else if (at >= tokens.Count || found.Position != tokens[at].Position)
                yield return $"{position} finds {Describe(found)}, and the walk has {Describe(tokens[at])}";
        }

        // The end of the file has no text, and the last token is the answer there.
        var end = tree.Root.FindToken(tree.Text.Length);
        if (end.Position != tokens[^1].Position)
            yield return $"the end of the file finds {Describe(end)}, not the last token {Describe(tokens[^1])}";
    }

    /// <summary>
    /// The tokens under the root make up the file. Each of them appears once, in source order,
    /// and their text with their trivia is the file's own text.
    /// </summary>
    private static IEnumerable<string> TheTokensOfTheRootAreTheFile(SyntaxTree tree)
    {
        var joined = new StringBuilder();
        var at = 0;
        foreach (var token in tree.Root.DescendantTokens())
        {
            if (token.Position != at)
                yield return $"{Describe(token)} starts at {token.Position}, and the token before it ended at {at}";
            at = token.FullSpan.End;
            joined.Append(token.ToFullString());
        }
        if (joined.ToString() != tree.Text)
            yield return "the tokens do not give back the file's text";
    }

    /// <summary>
    /// Stepping from token to token, forwards and back, walks the same tokens in the same order
    /// as the walk over the tree, missing ones included, and stops at either end of the file.
    /// </summary>
    private static IEnumerable<string> SteppingFromATokenWalksTheFile(SyntaxTree tree)
    {
        var tokens = tree.Root.DescendantTokens().ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var next = tokens[i].GetNextToken();
            var wanted = i + 1 < tokens.Count ? tokens[i + 1] : (SyntaxToken?)null;
            if (!Same(next, wanted))
                yield return $"after {Describe(tokens[i])} comes {Describe(next)}, not {Describe(wanted)}";

            var previous = tokens[i].GetPreviousToken();
            var before = i > 0 ? tokens[i - 1] : (SyntaxToken?)null;
            if (!Same(previous, before))
                yield return $"before {Describe(tokens[i])} comes {Describe(previous)}, not {Describe(before)}";
        }
    }

    /// <summary>
    /// Every node of the file is found again by the range it covers, and every node and token is
    /// held by the node it hangs from.
    /// </summary>
    private static IEnumerable<string> EveryNodeIsFoundByItsSpanAndHeldByItsParent(SyntaxTree tree)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (tree.Root.FindNode(node.Span) is var found && found.Span != node.Span)
                yield return $"{node.Kind} at {node.Span} is found as {found.Kind} at {found.Span}";
            if (node.Parent is not { } parent || !parent.FullSpan.Contains(node.FullSpan))
                yield return $"{node.Kind} at {node.FullSpan} is not held by {node.Parent?.Kind}";
            foreach (var child in node.ChildNodesAndTokens())
            {
                if (child.AsNode() is { } inner && !ReferenceEquals(inner.Parent, node))
                    yield return $"{inner.Kind} under {node.Kind} hangs from {inner.Parent?.Kind}";
            }
        }
    }

    /// <summary>Returns whether two answers are the same token, or both no token at all.</summary>
    private static bool Same(SyntaxToken? left, SyntaxToken? right) =>
        (left is null && right is null)
        || (left is { } one && right is { } other && one.Position == other.Position && one.Kind == other.Kind);

    /// <summary>
    /// Returns a token's name as a failure message gives it, or "nothing" where there is no token.
    /// </summary>
    private static string Describe(SyntaxToken? token) =>
        token is { } found ? $"`{found.Text}` ({found.Kind} at {found.FullSpan})" : "nothing";
}
