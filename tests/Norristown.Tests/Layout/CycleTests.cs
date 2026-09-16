using Norristown.Layout;
using Norristown.Project;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// How long each instruction takes. The count is an interval wherever it depends on
/// something the program does not say: whether an indexed read crosses a page, whether a
/// branch is taken, and on the 65C02 whether the decimal flag is set.
/// <para>
/// The ca65 oracle checks lengths, not timings, so the reference values below are the only
/// thing standing behind this table.
/// </para>
/// </summary>
public sealed class CycleTests
{
    [Theory]
    // The 6502, by addressing mode.
    [InlineData("lda", AddressingMode.Immediate, "2")]
    [InlineData("lda", AddressingMode.Direct, "3")]
    [InlineData("lda", AddressingMode.DirectX, "4")]
    [InlineData("lda", AddressingMode.Absolute, "4")]
    [InlineData("ldx", AddressingMode.DirectY, "4")]
    [InlineData("lda", AddressingMode.DirectIndirectX, "6")]

    // An indexed read pays for a page crossing only when it crosses one; a store always
    // pays, because it cannot begin until the address is settled.
    [InlineData("lda", AddressingMode.AbsoluteX, "4-5")]
    [InlineData("lda", AddressingMode.AbsoluteY, "4-5")]
    [InlineData("lda", AddressingMode.DirectIndirectY, "5-6")]
    [InlineData("sta", AddressingMode.AbsoluteX, "5")]
    [InlineData("sta", AddressingMode.AbsoluteY, "5")]
    [InlineData("sta", AddressingMode.DirectIndirectY, "6")]

    // Read, change and write back: always the long way round.
    [InlineData("asl", AddressingMode.Accumulator, "2")]
    [InlineData("asl", AddressingMode.Direct, "5")]
    [InlineData("asl", AddressingMode.DirectX, "6")]
    [InlineData("asl", AddressingMode.Absolute, "6")]
    [InlineData("asl", AddressingMode.AbsoluteX, "7")]
    [InlineData("inc", AddressingMode.Absolute, "6")]

    [InlineData("inx", AddressingMode.Implied, "2")]
    [InlineData("pha", AddressingMode.Implied, "3")]
    [InlineData("php", AddressingMode.Implied, "3")]
    [InlineData("pla", AddressingMode.Implied, "4")]
    [InlineData("plp", AddressingMode.Implied, "4")]
    [InlineData("rts", AddressingMode.Implied, "6")]
    [InlineData("rti", AddressingMode.Implied, "6")]
    [InlineData("brk", AddressingMode.Immediate, "7")]
    [InlineData("jmp", AddressingMode.Absolute, "3")]
    [InlineData("jsr", AddressingMode.Absolute, "6")]

    // The 6502's indirect jump reads its pointer without carrying into the high byte.
    [InlineData("jmp", AddressingMode.AbsoluteIndirect, "5")]

    // Two not taken, three taken, and one more when a taken branch crosses a page.
    [InlineData("beq", AddressingMode.Relative, "2-4")]
    [InlineData("bcc", AddressingMode.Relative, "2-4")]
    public void The6502TakesAsLongAsItsTableSays(string mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Mos6502, mnemonic, mode)?.ToString());
    }

    [Theory]
    // What the 65C02 keeps.
    [InlineData("lda", AddressingMode.Direct, "3")]
    [InlineData("lda", AddressingMode.AbsoluteX, "4-5")]
    [InlineData("asl", AddressingMode.Direct, "5")]

    // What it adds.
    [InlineData("lda", AddressingMode.DirectIndirect, "5")]
    [InlineData("sta", AddressingMode.DirectIndirect, "5")]
    [InlineData("bra", AddressingMode.Relative, "3-4")]
    [InlineData("phx", AddressingMode.Implied, "3")]
    [InlineData("ply", AddressingMode.Implied, "4")]
    [InlineData("stz", AddressingMode.Direct, "3")]
    [InlineData("stz", AddressingMode.AbsoluteX, "5")]
    [InlineData("trb", AddressingMode.Direct, "5")]
    [InlineData("tsb", AddressingMode.Absolute, "6")]
    [InlineData("inc", AddressingMode.Accumulator, "2")]
    [InlineData("bit", AddressingMode.Immediate, "2")]
    [InlineData("rmb3", AddressingMode.Direct, "5")]
    [InlineData("bbr0", AddressingMode.DirectRelative, "5-7")]
    [InlineData("stp", AddressingMode.Implied, "3")]

    // What it fixes: the indirect jump costs a cycle and reads the pointer properly, and a
    // shift indexed absolutely pays for a page crossing only when it crosses one.
    [InlineData("jmp", AddressingMode.AbsoluteIndirect, "6")]
    [InlineData("jmp", AddressingMode.AbsoluteIndirectX, "6")]
    [InlineData("asl", AddressingMode.AbsoluteX, "6-7")]
    [InlineData("inc", AddressingMode.AbsoluteX, "7")]

    // Decimal arithmetic costs one more, and nothing in the program says whether the
    // decimal flag is set where the instruction runs.
    [InlineData("adc", AddressingMode.Immediate, "2-3")]
    [InlineData("sbc", AddressingMode.Absolute, "4-5")]
    [InlineData("adc", AddressingMode.AbsoluteX, "4-6")]
    [InlineData("and", AddressingMode.Immediate, "2")]
    public void The65C02TakesAsLongAsItsTableSays(string mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65C02, mnemonic, mode)?.ToString());
    }

    /// <summary>The count layout keeps is the one for the mode it chose.</summary>
    [Fact]
    public void LayoutKeepsTheCountForTheModeItChose()
    {
        var analysis = Analysis.Program(("main.nt65", """
            .zeropage {
            near:   .res 1
            }

            far:    .byte 0

            .proc p {
                lda near
                lda far,x
                rts
            }
            """));
        var layout = analysis.Layouts.Single();
        var lines = analysis.File("main.nt65").Tree.Root.DescendantNodes()
            .Select(node => node.Statement)
            .OfType<Norristown.Syntax.SyntaxNode>()
            .Where(statement => statement.Kind == Norristown.Syntax.SyntaxKind.InstructionStatement)
            .ToList();

        Assert.Equal("3", layout.AnyOf(lines[0])?.Cycles?.ToString());
        Assert.Equal("4-5", layout.AnyOf(lines[1])?.Cycles?.ToString());
        Assert.Equal("6", layout.AnyOf(lines[2])?.Cycles?.ToString());
    }

    /// <summary>
    /// A long branch costs what the form chosen for it costs. Short, it is the branch.
    /// Long, the condition that would have branched falls into a <c>jmp</c> instead.
    /// </summary>
    [Fact]
    public void ALongBranchCostsWhatItsFormCosts()
    {
        Assert.Equal("2-4", Cycles.OfLongBranch(inverted: false).ToString());
        Assert.Equal("3-5", Cycles.OfLongBranch(inverted: true).ToString());
    }

    /// <summary>A block runs all of it or none, so what it costs is the sum of its statements.</summary>
    [Fact]
    public void ABlockCostsWhatItsStatementsCost()
    {
        var analysis = Analysis.Program(("main.nt65", ".proc p {\n    ldx #0\n    inx\n    rts\n}\n"));
        var block = analysis.Flows.Single().Regions.Single().Blocks.Single();

        Assert.Equal("10", block.Cycles?.ToString());
    }
}
