using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class IncrementalTests
{
    /// <summary>
    /// How many of the replay's edits are made before their results are checked. The edits are a
    /// chain and are made one at a time, but each check concerns its own edit alone, so a batch of
    /// them is checked in parallel, and the batch size bounds how many trees are held meanwhile.
    /// </summary>
    private const int Batch = 64;

    private static readonly string Corpus = string.Concat(DesignCorpus.Blocks.Select(b => b.Text));

    [Fact]
    public void EditingOneLineRelexesOnlyThatLine()
    {
        var tree = SyntaxTree.Parse("main.nt65", Corpus);
        var line = tree.Lines.Length / 2;
        var edited = tree.WithChange(new TextChange(tree.LineStarts[line], 0, "  "));

        Assert.Equal(SyntaxDump.Full(SyntaxTree.Parse("main.nt65", edited.Text)), SyntaxDump.Full(edited));
        for (var i = 0; i < tree.Lines.Length; i++)
        {
            if (i == line)
            {
                Assert.NotSame(tree.Lines[i], edited.Lines[i]);
            }
            else
            {
                Assert.Same(tree.Lines[i], edited.Lines[i]);

                // A line's syntax depends on its tokens and the kind of block around it, and
                // neither changed, so the statement is the same object too.
                Assert.Same(tree.Parsed(i).Node, edited.Parsed(i).Node);
            }
        }
    }

    /// <summary>
    /// A broken line's diagnostics are part of the nodes its parse returns, so an edit somewhere
    /// else keeps them without parsing the line again. Their spans move with the line, because a
    /// green node holds a diagnostic's offset relative to itself.
    /// </summary>
    [Fact]
    public void AnEditElsewhereKeepsABrokenLineAndItsDiagnostics()
    {
        var tree = SyntaxTree.Parse("main.nt65", "nop\n.proc p   ; note\nrts\n");
        Assert.Equal(
            [new Span("main.nt65", 2, 8, 8)],
            tree.Diagnostics.Select(d => d.Span));

        // When a line is inserted above it, the statement is the same object, and the diagnostic
        // has moved down a line and nowhere else.
        var edited = tree.WithChange(new TextChange(0, 0, "  sei\n"));
        Assert.Same(tree.Lines[1], edited.Lines[2]);
        Assert.Same(tree.Parsed(1).Node, edited.Parsed(2).Node);
        Assert.Equal(
            [new Span("main.nt65", 3, 8, 8)],
            edited.Diagnostics.Select(d => d.Span));

        // Text inserted on the line above it leaves the diagnostic's line and column as they
        // were.
        var indented = tree.WithChange(new TextChange(0, 0, "    "));
        Assert.Same(tree.Parsed(1).Node, indented.Parsed(1).Node);
        Assert.Equal(tree.Diagnostics, indented.Diagnostics);
    }

    /// <summary>
    /// A line whose enclosing block changes kind is parsed again, because that is the one
    /// thing outside a line that its syntax depends on.
    /// </summary>
    [Fact]
    public void ALineIsParsedAgainWhenItsBlockChangesKind()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".scope s {\ngreen = 5\n}\n");
        Assert.Equal(SyntaxKind.ConstantDeclaration, tree.Parsed(1).Node.Kind);

        // `.scope s {` becomes `.enum s {`, and the same line is now a member of it.
        var edited = tree.WithChange(new TextChange(0, 6, ".enum"));
        Assert.Same(tree.Lines[1], edited.Lines[1]);
        Assert.Equal(SyntaxKind.EnumMember, edited.Parsed(1).Node.Kind);
        Assert.Equal(SyntaxDump.Full(SyntaxTree.Parse("main.nt65", edited.Text)), SyntaxDump.Full(edited));
    }

    [Theory]
    [InlineData("rts\rnop\n", 4, 0, "\n")]      // \r then \n joins into one line break
    [InlineData("rts\r\nnop\n", 4, 1, "")]      // deleting the \n of \r\n leaves a lone \r
    [InlineData("rts\r\nnop\n", 3, 1, "")]      // deleting the \r
    [InlineData("rts\nnop\n", 3, 0, "\r")]      // \r inserted before \n
    [InlineData("rts\nnop", 7, 0, " {")]        // appending to a last line with no break
    [InlineData("rts\nnop\n", 8, 0, "x")]       // typing on the empty last line
    [InlineData("rts\nnop\n", 3, 1, "")]        // joining two lines
    [InlineData("a {\n}\n", 0, 6, "")]          // deleting everything
    [InlineData("", 0, 0, ".proc p {\n")]       // typing into an empty file
    public void EditsAtLineBoundariesMatchAFullParse(string text, int start, int length, string insert)
    {
        var edited = SyntaxTree.Parse("main.nt65", text).WithChange(new TextChange(start, length, insert));
        Assert.Equal(SyntaxDump.Full(SyntaxTree.Parse("main.nt65", edited.Text)), SyntaxDump.Full(edited));
    }

    /// <summary>
    /// Replays random edits, including ones that join, split and half-delete line breaks,
    /// and after each compares the incremental tree with a full parse of the same text.
    /// <para>
    /// Making an edit costs almost nothing and comparing what it gave with a full parse costs
    /// nearly all of the replay, so the edits are made a batch at a time and the batch's steps are
    /// compared in parallel. The step reported is still the earliest one that has anything wrong
    /// with it, which is the one a step-by-step replay would have stopped at.
    /// </para>
    /// </summary>
    [Fact]
    public void RandomEditsMatchAFullParse()
    {
        string[] inserts = ["", " ", "\n", "\r\n", "\r", "{", "}", "m!({", ".proc p {", "lda #1", "; c", "'", "\"", "$", "@"];
        var random = new Random(6502);
        var tree = SyntaxTree.Parse("main.nt65", Corpus.Replace("\n.proc", "\r\n.proc"));
        var batch = new List<Step>();

        for (var step = 0; step < 400; step++)
        {
            var start = random.Next(tree.Text.Length + 1);
            var length = random.Next(Math.Min(6, tree.Text.Length - start) + 1);
            var insert = random.Next(4) == 0
                ? tree.Text.Substring(random.Next(tree.Text.Length - 20), random.Next(20))
                : inserts[random.Next(inserts.Length)];
            var change = new TextChange(start, length, insert);
            var edited = tree.WithChange(change);
            batch.Add(new Step(step, change, tree, edited));
            tree = edited;

            if (batch.Count < Batch)
                continue;
            Compare(batch);
            batch.Clear();
        }
        Compare(batch);
    }

    /// <summary>
    /// The edits of one keystroke arrive together, and applying them in one pass gives the same
    /// tree as applying them one at a time. The changes are random, in runs of up to five, and
    /// each one's position refers to the text the earlier ones in its run left, as a client sends
    /// them.
    /// </summary>
    [Fact]
    public void ChangesTogetherMatchTheSameChangesOneAtATime()
    {
        string[] inserts = ["", " ", "\n", "\r\n", "\r", "{", "}", ".proc p {", "lda #1", "; c"];
        var random = new Random(65816);
        var tree = SyntaxTree.Parse("main.nt65", Corpus);
        var batch = new List<Step>();

        for (var step = 0; step < 200; step++)
        {
            var one = tree;
            var changes = new List<TextChange>();
            for (var i = random.Next(1, 6); i > 0; i--)
            {
                var start = random.Next(one.Text.Length + 1);
                var change = new TextChange(
                    start, random.Next(Math.Min(8, one.Text.Length - start) + 1), inserts[random.Next(inserts.Length)]);
                changes.Add(change);
                one = one.WithChange(change);
            }
            var together = tree.WithChanges(changes);
            batch.Add(new Step(step, changes[0], one, together));
            tree = together;

            if (batch.Count < Batch)
                continue;
            Together(batch);
            batch.Clear();
        }
        Together(batch);
    }

    [Fact]
    public void NoChangesLeaveTheTreeAsItIs()
    {
        var tree = SyntaxTree.Parse("main.nt65", "nop\nrts\n");
        Assert.Same(tree, tree.WithChanges([]));
    }

    /// <summary>Compares each step's one-at-a-time tree with its all-at-once one.</summary>
    private static void Together(List<Step> batch)
    {
        var failures = Repo.CollectFailures(batch, step =>
        {
            var (index, _, one, together) = step;
            if (one.Text != together.Text)
                return [$"step {index}: the text differs from applying the changes one at a time"];
            var dump = SyntaxDump.Full(together);
            return dump == SyntaxDump.Full(one) && dump == SyntaxDump.Full(SyntaxTree.Parse("main.nt65", together.Text))
                ? []
                : new[] { $"step {index}: the tree differs from applying the changes one at a time" };
        });
        if (failures.Count > 0)
            Assert.Fail(failures[0]);
    }

    /// <summary>Checks a batch's steps in parallel and fails on the earliest bad one.</summary>
    private static void Compare(List<Step> batch)
    {
        var failures = Repo.CollectFailures(batch, step => Problems(step).Take(1));
        if (failures.Count > 0)
            Assert.Fail(failures[0]);
    }

    /// <summary>Returns what is wrong with one step of the replay, if anything.</summary>
    private static IEnumerable<string> Problems(Step step)
    {
        var (index, change, before, after) = step;
        var expected = SyntaxTree.Parse("main.nt65", after.Text);
        if (SyntaxDump.Full(expected) != SyntaxDump.Full(after))
            yield return $"step {index}: {change} gives a different tree than a full parse";

        // Lines clear of the change keep their nodes.
        var shift = after.Lines.Length - before.Lines.Length;
        for (var i = 0; i < before.Lines.Length; i++)
        {
            var end = i + 1 < before.Lines.Length ? before.LineStarts[i + 1] : before.Text.Length;
            if (end < change.Start)
            {
                if (!ReferenceEquals(before.Lines[i], after.Lines[i]))
                    yield return $"step {index}: {change} parsed line {i + 1} again, and it is before the edit";
            }
            else if (before.LineStarts[i] > change.Start + change.Length)
            {
                if (!ReferenceEquals(before.Lines[i], after.Lines[i + shift]))
                    yield return $"step {index}: {change} parsed line {i + 1} again, and it is after the edit";
            }
        }
    }

    /// <summary>
    /// Represents one edit of the replay, with the tree it was made on and the tree it produced.
    /// </summary>
    private readonly record struct Step(int Index, TextChange Change, SyntaxTree Before, SyntaxTree After);
}
