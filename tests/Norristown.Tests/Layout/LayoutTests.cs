using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>Addressing-mode selection and instruction and data lengths.</summary>
public sealed class LayoutTests
{
    /// <summary>
    /// Layout picks the narrowest mode the instruction has that is at least as wide as the
    /// operand, unless an <c>a:</c> or <c>z:</c> prefix in the source picks one.
    /// </summary>
    [Theory]
    [InlineData("lda ptr", AddressingMode.Direct, 2)]
    [InlineData("lda buf", AddressingMode.Absolute, 3)]
    [InlineData("lda a:ptr", AddressingMode.Absolute, 3)]
    [InlineData("lda z:ptr", AddressingMode.Direct, 2)]
    [InlineData("lda $10", AddressingMode.Direct, 2)]
    [InlineData("lda $1234", AddressingMode.Absolute, 3)]
    [InlineData("lda ptr+1", AddressingMode.Direct, 2)]
    [InlineData("lda ptr,x", AddressingMode.DirectX, 2)]
    [InlineData("lda buf,x", AddressingMode.AbsoluteX, 3)]
    [InlineData("lda ptr,y", AddressingMode.AbsoluteY, 3)]
    [InlineData("ldx ptr,y", AddressingMode.DirectY, 2)]
    [InlineData("lda (ptr),y", AddressingMode.DirectIndirectY, 2)]
    [InlineData("lda (ptr,x)", AddressingMode.DirectIndirectX, 2)]
    [InlineData("lda #$10", AddressingMode.Immediate, 2)]
    [InlineData("jmp (ptr)", AddressingMode.AbsoluteIndirect, 3)]
    [InlineData("jmp here", AddressingMode.Absolute, 3)]
    [InlineData("bne here", AddressingMode.Relative, 2)]
    [InlineData("inx", AddressingMode.Implied, 1)]
    [InlineData("asl", AddressingMode.Accumulator, 1)]
    [InlineData("asl a", AddressingMode.Accumulator, 1)]
    [InlineData("brk #0", AddressingMode.Immediate, 2)]
    public void ModesAreChosenByWidth(string instruction, AddressingMode mode, int length)
    {
        var (layout, statement) = Layout(instruction, Cpu.Mos6502);

        Assert.Empty(layout.Diagnostics);
        var line = layout.Of(statement);
        Assert.NotNull(line);
        Assert.Equal(mode, line.Mode);
        Assert.Equal(length, line.Length);
    }

    /// <summary>
    /// The choice is written into the output only where the instruction offers more than one
    /// width for that shape; where there is nothing to choose, nothing is written.
    /// </summary>
    [Theory]
    [InlineData("lda ptr", "z:")]
    [InlineData("lda buf", "a:")]
    [InlineData("lda buf,x", "a:")]
    [InlineData("ldx ptr,y", "z:")]
    [InlineData("lda ptr,y", null)]
    [InlineData("lda (ptr),y", null)]
    [InlineData("jmp here", null)]
    [InlineData("jsr here", null)]
    [InlineData("bne here", null)]
    [InlineData("lda #$10", null)]
    [InlineData("inx", null)]
    public void OnlyARealChoiceIsWrittenOut(string instruction, string? prefix)
    {
        var (layout, statement) = Layout(instruction, Cpu.Mos6502);

        Assert.Equal(prefix, layout.Of(statement)?.Prefix);
    }

    [Theory]
    [InlineData(".byte 1, 2, 3", 3)]
    [InlineData(".byte 'A'", 1)]
    [InlineData(".byte \"text\"", 4)]
    [InlineData(".byte 1, \"ab\", 2", 4)]
    [InlineData(".word $1234, ptr", 4)]
    [InlineData(".addr ptr", 2)]
    [InlineData(".faraddr ptr", 3)]
    [InlineData(".dword $12345678", 4)]
    [InlineData(".strz \"hello\"", 6)]
    [InlineData(".res 16", 16)]
    [InlineData(".res 16, $ff", 16)]
    [InlineData(".res SIXTEEN", 16)]
    public void DataDirectivesHaveTheirOwnLength(string directive, int length)
    {
        var (layout, statement) = Layout(directive, Cpu.Mos6502);

        Assert.Empty(layout.Diagnostics);
        Assert.Equal(length, layout.Of(statement)?.Length);
    }

    [Theory]
    [InlineData("stz ptr", "`stz` is not available on the 6502, and is on the 65sc02, r65c02, 65c02 and 65816")]
    [InlineData("jml (ptr)", "`jml` is not available on the 6502, and is on the 65816")]
    [InlineData("lda (ptr)", "`lda` does not take this operand on the 6502")]
    [InlineData("lda 3,s", "`lda` does not take this operand on the 6502")]
    [InlineData("stx ptr,x", "`stx` does not take this operand on the 6502")]
    [InlineData("lda #$1234", "an immediate is one byte, and $1234 does not fit")]
    [InlineData("jmp a:here", "`jmp` transfers control, and a control transfer is not sized by a prefix")]
    [InlineData(".res ptr", "a `.res` count must be a constant")]
    [InlineData(".byte 300", "$012c does not fit in this directive")]
    public void WhatTheCpuMakesWrongIsReported(string line, string message)
    {
        var (layout, _) = Layout(line, Cpu.Mos6502);

        Assert.Equal(message, Assert.Single(layout.Diagnostics).Message);
    }

    /// <summary>What the 65C02 adds is available there and nowhere earlier.</summary>
    [Theory]
    [InlineData("lda (ptr)", AddressingMode.DirectIndirect)]
    [InlineData("jmp (ptr,x)", AddressingMode.AbsoluteIndirectX)]
    [InlineData("stz ptr", AddressingMode.Direct)]
    [InlineData("bra here", AddressingMode.Relative)]
    [InlineData("inc a", AddressingMode.Accumulator)]
    [InlineData("bbr0 ptr, here", AddressingMode.DirectRelative)]
    [InlineData("bit #$80", AddressingMode.Immediate)]
    public void The65C02AddsToThe6502(string instruction, AddressingMode mode)
    {
        var (layout, statement) = Layout(instruction, Cpu.Wdc65C02);

        Assert.Empty(layout.Diagnostics);
        Assert.Equal(mode, layout.Of(statement)?.Mode);
    }

    /// <summary>
    /// The CMOS variants differ only in whole instructions: the 65SC02 has neither the Rockwell
    /// bit instructions nor <c>wai</c> and <c>stp</c>, the R65C02 adds the bit instructions,
    /// the WDC 65C02 has both, and the 65816 keeps <c>wai</c> and drops the bit instructions.
    /// </summary>
    [Theory]
    [InlineData(Cpu.Cmos65SC02, "phx", true)]
    [InlineData(Cpu.Cmos65SC02, "smb1 ptr", false)]
    [InlineData(Cpu.Cmos65SC02, "wai", false)]
    [InlineData(Cpu.Rockwell65C02, "smb1 ptr", true)]
    [InlineData(Cpu.Rockwell65C02, "stp", false)]
    [InlineData(Cpu.Wdc65C02, "smb1 ptr", true)]
    [InlineData(Cpu.Wdc65C02, "wai", true)]
    [InlineData(Cpu.Wdc65816, "wai", true)]
    [InlineData(Cpu.Wdc65816, "smb1 ptr", false)]
    public void EachCmosVariantHasExactlyItsOwnInstructions(Cpu cpu, string instruction, bool has)
    {
        var (layout, _) = Layout(instruction, cpu);

        Assert.Equal(has, layout.Diagnostics.Count == 0);
    }

    /// <summary>A far target is an error on a CPU whose control transfers are all near.</summary>
    [Fact]
    public void AFarTargetIsNotNear()
    {
        var source = """
            .module main
            .segment FAR: far
            .segment FAR {
            .data away:   .byte
            }
            .segment CODE
            .proc p {
                jsr away
            }
            """;
        var tree = SyntaxTree.Parse("main.nt65", source);
        var model = SemanticModel.Create(tree, SegmentTable.Build([tree], []));

        var layout = CodeLayout.Create(model, Cpu.Mos6502);
        Assert.Equal("`jsr` takes a near target, and this one is far",
            Assert.Single(layout.Diagnostics).Message);
    }

    /// <summary>
    /// A data directive with no name in front of it opens its body just as a named one does:
    /// its own line takes no bytes, and the lines of the body carry them.
    /// </summary>
    [Fact]
    public void ADirectiveWithNoNameOpensItsBody()
    {
        var main = Analysis.Outputs(("main.nt65",
            ".module main\n.segment CODE\n.proc p {\n    rts\n.byte[] {\n    1, 2\n    3\n}\n}\n"))["main.s"];

        Assert.Contains("    rts\n    .byte 1, 2\n    .byte 3\n", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tokens the parser had to skip are not values of the directive they were found on, so a
    /// line whose stray tokens were already reported is not also reported as having one
    /// element too many.
    /// </summary>
    [Theory]
    [InlineData(".data d {\n.byte[2] 1, 2 ]\n}", "the values of an array go in braces: `.byte[n] { 1, 2 }`")]
    [InlineData(".data d {\n.strz \"ab\" ]\n}", "unexpected `]`")]
    [InlineData(".data d: .byte[3] {\n    1, 2 )\n    3\n}", "unexpected `)`")]
    public void SkippedTokensAreNoElementOfTheirDirective(string data, string message)
    {
        var program = Analysis.Program(("main.nt65", $".module main\n.segment RODATA\n{data}\n.export d\n"));

        Assert.EndsWith($": {message}", Assert.Single(program.Problems()), StringComparison.Ordinal);
    }

    /// <summary>Lays out one line, with a few symbols around it to point at.</summary>
    private static (CodeLayout Layout, SyntaxNode Statement) Layout(string line, Cpu cpu)
    {
        var source = $$"""
            .module main
            SIXTEEN = 16
            .segment ZEROPAGE
            .data ptr:    .byte[2]
            .segment BSS
            .data buf:    .byte[256]
            .proc p {
            here:
                {{line}}
            }
            """;
        var tree = SyntaxTree.Parse("main.nt65", source);
        var model = SemanticModel.Create(tree, SegmentTable.Build([tree], []));
        Assert.Empty(model.Diagnostics);
        Assert.Empty(tree.Diagnostics);

        var layout = CodeLayout.Create(model, cpu);
        var statement = tree.Root.DescendantNodes()
            .Last(node => node is InstructionStatementSyntax or DataDirectiveSyntax);
        return (layout, statement);
    }
}
