using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class LineKindTests
{
    [Theory]
    [InlineData("", LineKind.Blank)]
    [InlineData("   ; a comment", LineKind.Blank)]
    [InlineData(".word 1, 2", LineKind.Directive)]
    [InlineData("    .proc draw {", LineKind.Directive)]
    [InlineData("loop: lda #1", LineKind.Label)]
    [InlineData("@loop:", LineKind.Label)]
    [InlineData("z:", LineKind.Label)]
    [InlineData("x: .word", LineKind.Label)] // a struct member may be named like a register
    [InlineData("boss:   .tag Actor { x = 100 }", LineKind.Label)]
    [InlineData("SCREEN = $0400", LineKind.Constant)]
    [InlineData("@n = 1", LineKind.Constant)]
    [InlineData("x = 16", LineKind.Constant)] // an initializer value for a member named x
    [InlineData("set16!(ptr, SCREEN)", LineKind.MacroCall)]
    [InlineData("if!(cs) {", LineKind.MacroCall)]
    [InlineData("red", LineKind.BareIdentifier)]
    [InlineData("  body    ; splice", LineKind.BareIdentifier)]
    [InlineData("lda #1", LineKind.Instruction)]
    [InlineData("jeq @far", LineKind.Instruction)]
    [InlineData("asl", LineKind.Instruction)]
    [InlineData("cmd_move - 1, cmd_fire - 1", LineKind.Expression)]
    [InlineData("gfx::init", LineKind.Expression)]
    [InlineData("@done", LineKind.Expression)]
    [InlineData("x", LineKind.Expression)]
    [InlineData("42", LineKind.Expression)]
    [InlineData("}", LineKind.BlockClose)]
    [InlineData("} .elseif LEVEL > 2 {", LineKind.BlockClose)]
    [InlineData("} else {", LineKind.BlockClose)]
    public void ClassifiedByTheFirstTokens(string line, LineKind kind) => Assert.Equal(kind, Lexer.LexLine(line).LineKind);
}

public sealed class BlockTests
{
    [Theory]
    [InlineData(".proc f {", 1, BlockKind.Proc)]
    [InlineData(".proc f: a16, i8 -> a8, i8 {   ; comment", 1, BlockKind.Proc)]
    [InlineData(".SCOPE {", 1, BlockKind.Scope)]
    [InlineData(".macro if(c: one(eq, ne), then: block, else: block = {}) {", 1, BlockKind.Macro)]
    [InlineData(".rodata {", 1, BlockKind.Segment)]
    [InlineData(".segment \"ZP2\" {", 1, BlockKind.Segment)]
    [InlineData("hero: .tag Actor {", 1, BlockKind.TagInitializer)]
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
    [InlineData("boss: .tag Actor { x = 100, y = 40 }", 0, BlockKind.None)]
    [InlineData("lda #1 ; {", 0, BlockKind.None)]
    [InlineData(".byte '{'", 0, BlockKind.None)]
    public void BraceValueAndKind(string line, int value, BlockKind kind)
    {
        var green = Lexer.LexLine(line);
        Assert.Equal(value, green.BraceValue);
        Assert.Equal(kind, green.OpensBlockKind);
    }

    private static SyntaxTree Parse(params string[] lines) => SyntaxTree.Parse("main.nt65", string.Join("\n", lines) + "\n");

    private static string[] Messages(SyntaxTree tree) =>
        [.. tree.Diagnostics.Select(d => $"{d.Span.Line}:{d.Span.StartColumn}: {d.Message}")];

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
        ".rodata {",
        "table: .byte 1",
        "}",
        ".proc c {",
        "    rts",
        "}",
    ];

    private const string ThreeProcsBlocks = """
        Proc 1-3
        Proc 4-9
          Scope 5-7
        Segment 10-12
        Proc 13-15

        """;

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
        Assert.Empty(tree.Diagnostics);
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
}
