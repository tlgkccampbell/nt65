using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class BlockTests
{
    private const string ThreeProcsBlocks = """
        Proc 1-3
        Proc 4-9
          Scope 5-7
        Segment 10-12
        Proc 13-15

        """;

    private static readonly string[] ThreeProcs =
    [
        ".proc a {",
        "    rts",
        "}",
        ".proc b {",
        "    .scope {",
        "        nop",
        "    }",
        "    rts",
        "}",
        ".segment RODATA {",
        ".data table: .byte 1",
        "}",
        ".proc c {",
        "    rts",
        "}",
    ];

    [Theory]
    [InlineData(".proc f {", 1, BlockKind.Proc)]
    [InlineData(".proc f: a16, i8 -> a8, i8 {   ; comment", 1, BlockKind.Proc)]
    [InlineData(".SCOPE {", 1, BlockKind.Scope)]
    [InlineData(".macro if(c: one(eq, ne), then: block, else: block = {}) {", 1, BlockKind.Macro)]
    [InlineData(".segment ZP2 {", 1, BlockKind.Segment)]
    [InlineData(".segment ZP2", 0, BlockKind.Region)]
    [InlineData(".segment ZP2: zp", 0, BlockKind.None)]
    [InlineData(".data hero: .type Actor {", 1, BlockKind.RecordInitializer)]
    [InlineData(".data heroes: .type Actor[] {", 1, BlockKind.DataBody)]
    [InlineData(".data row_lo: .byte[ROWS] {", 1, BlockKind.DataBody)]
    [InlineData(".data vectors {", 1, BlockKind.Data)]
    [InlineData("    .word[] {", 1, BlockKind.DataBody)]
    [InlineData("if!(cs) {", 1, BlockKind.MacroBlock)]
    [InlineData("tune: note!(C4) {", 1, BlockKind.MacroBlock)]
    [InlineData("m!({buf,x}) {", 1, BlockKind.MacroBlock)]
    [InlineData(".frobnicate {", 1, BlockKind.Unknown)]
    [InlineData("{", 1, BlockKind.Unknown)] // no bare blocks; the parser reports it
    [InlineData("}", -1, BlockKind.None)]
    [InlineData("} .else {", 0, BlockKind.If)]
    [InlineData("} .elseif x == 1 {", 0, BlockKind.If)]
    [InlineData("} else {", 0, BlockKind.MacroBlock)]
    [InlineData("m!({", 0, BlockKind.None)] // the brace is inside an open parenthesis
    [InlineData("m!((x), {", 0, BlockKind.None)]
    [InlineData(".data boss: .type Actor { x = 100, y = 40 }", 0, BlockKind.None)]
    [InlineData("lda #1 ; {", 0, BlockKind.None)]
    [InlineData(".byte '{'", 0, BlockKind.None)]
    public void BraceValueAndKind(string line, int value, BlockKind kind)
    {
        var green = Lexer.LexLine(line);
        Assert.Equal(value, green.BraceValue);
        Assert.Equal(kind, green.OpensBlockKind);
    }

    [Fact]
    public void ContinuationLinesChainSiblingBlocks()
    {
        var tree = Parse(
            ".proc main {",
            "    .if DEBUG {",
            "        jsr trace",
            "    } .elseif LEVEL > 2 {",
            "        if!(cs) {",
            "            lda #0",
            "        } else {",
            "            inx",
            "        }",
            "    } .else {",
            "    }",
            "}");
        Assert.Equal("""
            Proc 1-12
              If 2-3 no closer
              If 4-9 no closer
                MacroBlock 5-6 no closer
                MacroBlock 7-9
              If 10-11

            """, SyntaxDump.Blocks(tree));
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(tree.Text, tree.Root.ToFullString());
    }

    [Fact]
    public void AHalfTypedMacroCallSwallowsNothing()
    {
        Assert.Equal(ThreeProcsBlocks, SyntaxDump.Blocks(Parse(ThreeProcs)));

        string[] edited = [.. ThreeProcs[..5], "        m!({", .. ThreeProcs[5..]];
        var tree = Parse(edited);
        Assert.Equal("""
            Proc 1-3
            Proc 4-10
              Scope 5-8
            Segment 11-13
            Proc 14-16

            """, SyntaxDump.Blocks(tree));

        // The line itself is unreadable, and that is all it is: one message, on it.
        Assert.Equal(["6:13: expected an expression"], Messages(tree));
    }

    [Fact]
    public void AMissingCloseBraceIsContainedByTheNextProc()
    {
        // Line 7, the scope's `}`, is deleted: the scope takes proc b's `}` and b is left open.
        string[] edited = [.. ThreeProcs[..6], .. ThreeProcs[7..]];
        var tree = Parse(edited);
        Assert.Equal("""
            Proc 1-3
            Proc 4-11 no closer
              Scope 5-8
              Segment 9-11
            Proc 12-14

            """, SyntaxDump.Blocks(tree));
        Assert.Equal(["4:9: missing `}` to close this block"], Messages(tree));
    }

    [Fact]
    public void AnExtraCloseBraceIsReportedAndIgnored()
    {
        string[] edited = [.. ThreeProcs[..3], "}", .. ThreeProcs[3..]];
        var tree = Parse(edited);
        Assert.Equal("""
            Proc 1-3
            Proc 5-10
              Scope 6-8
            Segment 11-13
            Proc 14-16

            """, SyntaxDump.Blocks(tree));
        Assert.Equal(["4:1: unmatched `}`"], Messages(tree));
    }

    [Fact]
    public void BlocksLeftOpenAtTheEndAreReported()
    {
        var tree = Parse(".proc a {", "    .if X {", "    rts");
        Assert.Equal("Proc 1-3 no closer\n  If 2-3 no closer\n", SyntaxDump.Blocks(tree));
        Assert.Equal(["1:9: missing `}` to close this block", "2:11: missing `}` to close this block"],
            Messages(tree).Order());
    }

    [Fact]
    public void BalancedBracesKeepANestedProcNested()
    {
        // Procs do not nest, but with balanced braces the structure is as written, so the
        // parser can say that rather than the block layer guessing.
        var tree = Parse(".proc a {", "    .proc b {", "    }", "}");
        Assert.Equal("Proc 1-4\n  Proc 2-3\n", SyntaxDump.Blocks(tree));
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void LexicalDiagnosticsHaveLineAndColumns()
    {
        var tree = SyntaxTree.Parse("main.nt65", "lda #1\r\n    .byte $1G, 'ab'\n");
        Assert.Equal(
            [new Span("main.nt65", 2, 11, 14), new Span("main.nt65", 2, 16, 20)],
            tree.Diagnostics.Select(d => d.Span));
    }

    private static SyntaxTree Parse(params string[] lines) => SyntaxTree.Parse("main.nt65", string.Join("\n", lines) + "\n");

    private static string[] Messages(SyntaxTree tree) =>
        [.. tree.Diagnostics.Select(d => $"{d.Span.Line}:{d.Span.StartColumn}: {d.Message}")];
}
