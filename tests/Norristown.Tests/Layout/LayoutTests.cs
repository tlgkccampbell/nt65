using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>Addressing-mode selection and instruction and data lengths.</summary>
public sealed class LayoutTests
{
    /// <summary>The narrowest mode at least as wide as the operand, whatever the source wrote.</summary>
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
    [InlineData(".asciiz \"hello\"", 6)]
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
    [InlineData("stz ptr", "`stz` is a 65C02 instruction, and this program is built for the 6502")]
    [InlineData("jml (ptr)", "`jml` is not available on the 6502")]
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

    /// <summary>A far target is an error on a CPU whose control transfers are all near.</summary>
    [Fact]
    public void AFarTargetIsNotNear()
    {
        var source = """
            .segment "FAR": far
            .segment "FAR" {
            away:   .res 1
            }
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

    /// <summary>Lays out one line, with a few symbols around it to point at.</summary>
    private static (CodeLayout Layout, SyntaxNode Statement) Layout(string line, Cpu cpu)
    {
        var source = $$"""
            SIXTEEN = 16
            .zeropage {
            ptr:    .res 2
            }
            .bss {
            buf:    .res 256
            }
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
            .Last(node => node.Kind is SyntaxKind.InstructionStatement or SyntaxKind.DataDirective);
        return (layout, statement);
    }
}
