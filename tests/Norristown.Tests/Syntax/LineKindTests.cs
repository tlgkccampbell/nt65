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
    [InlineData("boss:   .type Actor { x = 100 }", LineKind.Label)]
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
