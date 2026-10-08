using Norristown.Processor;
using Norristown.Project;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// Tests <c>.encoded</c>, which gives the opcode byte the instruction below it is written as,
/// where the CPU runs several bytes as that instruction and ca65 writes another.
/// </summary>
public sealed class EncodedTests
{
    /// <summary>
    /// The byte and the operand's bytes are written in place of the instruction. The byte picks
    /// the form, so <c>$3C</c> makes <c>nop $39,x</c> absolute. The instruction is still code,
    /// so the analysis follows it and needs no <c>.next</c>.
    /// </summary>
    [Theory]
    [InlineData("$34", "nop $39,x", ".byte $34, $39")]
    [InlineData("$3C", "nop $39,x", ".byte $3C, $39, $00")]
    [InlineData("$1A", "nop", ".byte $1A")]
    [InlineData("$EB", "sbc #$12", ".byte $EB, $12")]
    [InlineData("$1C", "nop table,x", ".byte $1C, .lobyte(table), .hibyte(table)")]
    public void TheByteIsWrittenInPlaceOfTheInstruction(string opcode, string instruction, string written)
    {
        var program = Program($".encoded {opcode}\n    {instruction}\n    rts\n");
        Assert.Empty(program.Diagnostics);

        var output = Output($".encoded {opcode}\n    {instruction}\n    rts\n");
        Assert.Contains($"    {written}", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"; {instruction}", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A byte that runs as another instruction or form, a byte the CPU has no instruction for, a
    /// branch, a value that is not a byte, and an <c>.encoded</c> with no instruction below it
    /// are each reported.
    /// </summary>
    [Theory]
    [InlineData(Cpu.Mos6502X, ".encoded $EA\n    nop $39,x\n", "`.encoded $EA` cannot write `nop`: on the 6502x it runs as `nop` with no operand")]
    [InlineData(Cpu.Mos6502X, ".encoded $B5\n    nop $39,x\n", "`.encoded $B5` cannot write `nop`: on the 6502x it runs as `lda` in the `zpx` form")]
    [InlineData(Cpu.Mos6502, ".encoded $34\n    nop\n", "`.encoded $34` cannot write `nop`: the 6502 has no instruction with that opcode")]
    [InlineData(Cpu.Mos6502X, ".encoded $D0\n    bne @x\n@x:\n", "`.encoded $D0` cannot write `bne`: a branch or a block move has one encoding, the one ca65 writes")]
    [InlineData(Cpu.Mos6502X, ".encoded 300\n    nop\n", "`.encoded` takes the opcode byte the instruction below it is written as, a constant from 0 to 255")]
    [InlineData(Cpu.Mos6502X, ".encoded $EA\n    .byte 1\n", "`.encoded` applies to the instruction below it, and there is none")]
    public void AByteThatCannotWriteTheInstructionIsReported(Cpu cpu, string lines, string message)
    {
        var program = Program(lines + "    rts\n", cpu);

        Assert.Contains(message, program.Diagnostics.Select(d => d.Message));
    }

    private static ProgramAnalysis Program(string body, Cpu cpu = Cpu.Mos6502X) =>
        Analysis.Program(ProjectSettings.None with { Cpu = cpu }, ("main.nt65", Source(body)));

    private static string Output(string body) =>
        Analysis.Outputs(ProjectSettings.None with { Cpu = Cpu.Mos6502X }, ("main.nt65", Source(body)))["main.s"];

    private static string Source(string body) =>
        ".module main\n.segment CODE\n.export .proc main {\n    " + body + "}\n.segment RODATA\n.data table: .byte[4] {\n    1, 2, 3, 4\n}\n";
}
