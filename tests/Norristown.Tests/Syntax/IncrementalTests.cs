using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class IncrementalTests
{
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
    /// A broken line's diagnostics are part of the nodes its parse hands back, so an edit
    /// somewhere else keeps them without parsing the line again — and their spans move with the
    /// line, because what a green node holds is an offset within itself.
    /// </summary>
    [Fact]
    public void AnEditElsewhereKeepsABrokenLineAndItsDiagnostics()
    {
        var tree = SyntaxTree.Parse("main.nt65", "nop\n.proc p   ; note\nrts\n");
        Assert.Equal(
            [new Span("main.nt65", 2, 8, 8)],
            tree.Diagnostics.Select(d => d.Span));

        // A line inserted above it: the statement is the same object, and the diagnostic has
        // moved down a line and nowhere else.
        var edited = tree.WithChange(new TextChange(0, 0, "  sei\n"));
        Assert.Same(tree.Lines[1], edited.Lines[2]);
        Assert.Same(tree.Parsed(1).Node, edited.Parsed(2).Node);
        Assert.Equal(
            [new Span("main.nt65", 3, 8, 8)],
            edited.Diagnostics.Select(d => d.Span));

        // And text inserted on the line above it, which shifts nothing at all.
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
    /// </summary>
    [Fact]
    public void RandomEditsMatchAFullParse()
    {
        string[] inserts = ["", " ", "\n", "\r\n", "\r", "{", "}", "m!({", ".proc p {", "lda #1", "; c", "'", "\"", "$", "@"];
        var random = new Random(6502);
        var tree = SyntaxTree.Parse("main.nt65", Corpus.Replace("\n.proc", "\r\n.proc"));

        for (var step = 0; step < 400; step++)
        {
            var start = random.Next(tree.Text.Length + 1);
            var length = random.Next(Math.Min(6, tree.Text.Length - start) + 1);
            var insert = random.Next(4) == 0
                ? tree.Text.Substring(random.Next(tree.Text.Length - 20), random.Next(20))
                : inserts[random.Next(inserts.Length)];
            var change = new TextChange(start, length, insert);
            var edited = tree.WithChange(change);

            var expected = SyntaxTree.Parse("main.nt65", edited.Text);
            if (SyntaxDump.Full(expected) != SyntaxDump.Full(edited))
                Assert.Fail($"step {step}: {change} gives a different tree than a full parse");

            // Lines clear of the change keep their nodes.
            var shift = edited.Lines.Length - tree.Lines.Length;
            for (var i = 0; i < tree.Lines.Length; i++)
            {
                var end = i + 1 < tree.Lines.Length ? tree.LineStarts[i + 1] : tree.Text.Length;
                if (end < change.Start)
                    Assert.Same(tree.Lines[i], edited.Lines[i]);
                else if (tree.LineStarts[i] > change.Start + change.Length)
                    Assert.Same(tree.Lines[i], edited.Lines[i + shift]);
            }
            tree = edited;
        }
    }
}
