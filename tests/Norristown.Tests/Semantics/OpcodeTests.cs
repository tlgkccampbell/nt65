using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Tests <c>.opcode</c>, the byte an instruction form is written as on the build's CPU, in code,
/// in data and in a build's conditions, and the problems it reports.
/// </summary>
public sealed class OpcodeTests
{
    /// <summary>
    /// An instruction with no operand needs no mode, and neither does one with only one form.
    /// The mode words are those <c>.mode</c> returns, with the direct page and <c>far</c> added.
    /// </summary>
    [Theory]
    [InlineData(Cpu.Mos6502, "dex", 0xCA)]
    [InlineData(Cpu.Mos6502, "asl", 0x0A)]
    [InlineData(Cpu.Mos6502, "bmi", 0x30)]
    [InlineData(Cpu.Mos6502, "jsr", 0x20)]
    [InlineData(Cpu.Mos6502, "lda, absx", 0xBD)]
    [InlineData(Cpu.Mos6502, "lda, zp", 0xA5)]
    [InlineData(Cpu.Mos6502, "LDA, IMM", 0xA9)]
    [InlineData(Cpu.Mos6502, "jmp, ind", 0x6C)]
    [InlineData(Cpu.Mos6502X, "nop, zpx", 0x14)]
    [InlineData(Cpu.Cmos65SC02, "lda, ind", 0xB2)]
    [InlineData(Cpu.Cmos65SC02, "jmp, indx", 0x7C)]
    [InlineData(Cpu.Wdc65816, "lda, far", 0xAF)]
    [InlineData(Cpu.Wdc65816, "lda, long", 0xA7)]
    [InlineData(Cpu.Wdc65816, "jml, long", 0xDC)]
    [InlineData(Cpu.Wdc65816, "mvn", 0x54)]
    public void AnOpcodeIsTheByteTheFormIsWrittenAs(Cpu cpu, string arguments, int opcode)
    {
        var outputs = Analysis.Outputs(ProjectSettings.None with { Cpu = cpu }, ("main.nt65",
            $".module main\n.segment RODATA\n.export .data table {{\n    .byte .opcode({arguments})\n}}\n"));

        Assert.Contains($".byte ${opcode:X2}", outputs["main.s"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A store of an opcode over an instruction says what it stores, and a condition may ask for one.</summary>
    [Fact]
    public void AnOpcodeIsAConstantAnywhere()
    {
        const string Text = """
            .module main
            .cpu 6502
            .if .opcode(bmi) == '0' {
            .const DIGIT = 1
            .export DIGIT
            }
            .segment CODE
            .export .proc main {
                ldy #.opcode(dex)
                sty @step
                .patch @step
            @step:
                inx
                rts
            }
            """;

        Assert.Empty(Analysis.Program(ProjectSettings.None, ("main.nt65", Text)).Problems());
    }

    /// <summary>
    /// A mnemonic the CPU lacks, a form it lacks, an instruction with several forms and no mode,
    /// and anything but a mnemonic and a mode word are each reported.
    /// </summary>
    [Theory]
    [InlineData("phx", "`.opcode(phx)` names no instruction the 6502 has: it has no `phx` [opcode-form]")]
    [InlineData("lda", "`.opcode(lda)` names no instruction the 6502 has: `lda` has several forms, so name one of `imm`, `zp`, `zpx`, `abs`, `absx`, `absy`, `indx`, `indy` [opcode-form]")]
    [InlineData("lda, zpy", "`.opcode(lda, zpy)` names no instruction the 6502 has: `lda` has no `zpy` form, only `imm`, `zp`, `zpx`, `abs`, `absx`, `absy`, `indx`, `indy` [opcode-form]")]
    [InlineData("dex, imm", "`.opcode(dex, imm)` names no instruction the 6502 has: `dex` takes no mode [opcode-form]")]
    [InlineData("jeq", "`.opcode(jeq)` names no instruction the 6502 has: `jeq` is written as one instruction or two, as the target's reach decides [opcode-form]")]
    [InlineData("lda, absz", "`.opcode` takes a mnemonic and, where it has several forms, a mode, such as `.opcode(dex)` or `.opcode(lda, absx)` [opcode-argument]")]
    [InlineData("LIMIT", "`.opcode` takes a mnemonic and, where it has several forms, a mode, such as `.opcode(dex)` or `.opcode(lda, absx)` [opcode-argument]")]
    public void AWrongCallIsReported(string arguments, string message)
    {
        var program = Analysis.Program(ProjectSettings.None with { Cpu = Cpu.Mos6502 }, ("main.nt65",
            $".module main\n.segment RODATA\n.export .data table {{\n    .byte .opcode({arguments})\n}}\n"));

        Assert.Equal([message], program.Diagnostics.Select(d => $"{d.Message} [{d.Id}]"));
    }
}
