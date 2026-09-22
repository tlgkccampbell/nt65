using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
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
        Assert.Equal(cycles, Cycles.Of(Cpu.Mos6502, mnemonic, mode)?.Count.ToString());
    }

    [Theory]
    // The 6502's own counts, unchanged.
    [InlineData("lda", AddressingMode.AbsoluteX, "4-5")]
    [InlineData("jmp", AddressingMode.AbsoluteIndirect, "5")]
    [InlineData("nop", AddressingMode.Implied, "2")]

    // A read-modify-write folded into an arithmetic instruction costs what the pair costs,
    // and pays the index cycle whatever it does.
    [InlineData("slo", AddressingMode.Direct, "5")]
    [InlineData("slo", AddressingMode.DirectX, "6")]
    [InlineData("rla", AddressingMode.Absolute, "6")]
    [InlineData("sre", AddressingMode.AbsoluteX, "7")]
    [InlineData("rra", AddressingMode.AbsoluteY, "7")]
    [InlineData("dcp", AddressingMode.DirectIndirectX, "8")]
    [InlineData("isc", AddressingMode.DirectIndirectY, "8")]

    // A load pays for a page crossing only when it crosses one, as the documented loads do.
    [InlineData("lax", AddressingMode.Immediate, "2")]
    [InlineData("lax", AddressingMode.Direct, "3")]
    [InlineData("lax", AddressingMode.DirectY, "4")]
    [InlineData("lax", AddressingMode.AbsoluteY, "4-5")]
    [InlineData("lax", AddressingMode.DirectIndirectY, "5-6")]
    [InlineData("las", AddressingMode.AbsoluteY, "4-5")]
    [InlineData("sax", AddressingMode.Absolute, "4")]
    [InlineData("sax", AddressingMode.DirectIndirectX, "6")]

    // The immediate-only opcodes, and the indexed stores, which settle the address first.
    [InlineData("alr", AddressingMode.Immediate, "2")]
    [InlineData("axs", AddressingMode.Immediate, "2")]
    [InlineData("sha", AddressingMode.AbsoluteY, "5")]
    [InlineData("sha", AddressingMode.DirectIndirectY, "6")]
    [InlineData("shx", AddressingMode.AbsoluteY, "5")]
    [InlineData("tas", AddressingMode.AbsoluteY, "5")]

    // `nop` reads an operand here, and its indexed form is a read like any other.
    [InlineData("nop", AddressingMode.Direct, "3")]
    [InlineData("nop", AddressingMode.AbsoluteX, "4-5")]
    public void The6502xTakesAsLongAsItsTableSays(string mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Mos6502X, mnemonic, mode)?.Count.ToString());
    }

    /// <summary>
    /// <c>jam</c> stops the processor: there is no next cycle to reach, so it is counted
    /// nowhere and the block it is in says why rather than quietly leaving it out.
    /// </summary>
    [Fact]
    public void JamHasNoCount() => Assert.Null(Cycles.Of(Cpu.Mos6502X, "jam", AddressingMode.Implied));

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
        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65C02, mnemonic, mode)?.Count.ToString());
    }

    [Theory]
    // The 8-bit forms, in native mode, match the 65C02's apart from decimal arithmetic,
    // which costs nothing more on the 65816.
    [InlineData("lda", AddressingMode.Immediate, "a8, i8, native", "2")]
    [InlineData("adc", AddressingMode.Immediate, "a8, i8, native", "2")]
    [InlineData("lda", AddressingMode.Absolute, "a8, i8, native", "4")]

    // A 16-bit register reads and writes a byte more, and a read-modify-write two.
    [InlineData("lda", AddressingMode.Immediate, "a16, i8, native", "3")]
    [InlineData("lda", AddressingMode.Absolute, "a16, i8, native", "5")]
    [InlineData("sta", AddressingMode.Absolute, "a16, i8, native", "5")]
    [InlineData("asl", AddressingMode.Absolute, "a16, i8, native", "8")]
    [InlineData("asl", AddressingMode.Accumulator, "a16, i8, native", "2")]
    [InlineData("ldx", AddressingMode.Immediate, "a16, i8, native", "2")]
    [InlineData("ldx", AddressingMode.Immediate, "a8, i16, native", "3")]

    // A width nobody knows covers both.
    [InlineData("lda", AddressingMode.Immediate, "a?, i8, native", "2-3")]

    // An indexed read pays for crossing a page only sometimes, unless the index is 16 bits,
    // when it always does.
    [InlineData("lda", AddressingMode.AbsoluteX, "a8, i8, native", "4-5")]
    [InlineData("lda", AddressingMode.AbsoluteX, "a8, i16, native", "5")]
    [InlineData("lda", AddressingMode.DirectIndirectY, "a8, i16, native", "6-7")]

    // A direct operand costs one more when D's low byte is not zero, which is not known.
    [InlineData("lda", AddressingMode.Direct, "a8, i8, native", "3-4")]

    // What only the 65816 has.
    [InlineData("lda", AddressingMode.Long, "a8, i8, native", "5")]
    [InlineData("lda", AddressingMode.LongX, "a16, i8, native", "6")]
    [InlineData("lda", AddressingMode.StackRelative, "a8, i8, native", "4")]
    [InlineData("lda", AddressingMode.StackRelativeIndirectY, "a8, i8, native", "7")]
    [InlineData("lda", AddressingMode.DirectIndirectLong, "a8, i8, native", "6-7")]
    [InlineData("jsl", AddressingMode.Long, "a8, i8, native", "8")]
    [InlineData("jml", AddressingMode.AbsoluteIndirectLong, "a8, i8, native", "6")]
    [InlineData("jsr", AddressingMode.AbsoluteIndirectX, "a8, i8, native", "8")]
    [InlineData("rtl", AddressingMode.Implied, "a8, i8, native", "6")]
    [InlineData("brl", AddressingMode.RelativeLong, "a8, i8, native", "4")]
    [InlineData("per", AddressingMode.RelativeLong, "a8, i8, native", "6")]
    [InlineData("pea", AddressingMode.Absolute, "a8, i8, native", "5")]
    [InlineData("rep", AddressingMode.Immediate, "a8, i8, native", "3")]
    [InlineData("xba", AddressingMode.Implied, "a8, i8, native", "3")]
    [InlineData("pha", AddressingMode.Implied, "a16, i8, native", "4")]
    [InlineData("phd", AddressingMode.Implied, "a8, i8, native", "4")]
    [InlineData("pld", AddressingMode.Implied, "a8, i8, native", "5")]

    // Native mode pushes and pulls the program bank as well.
    [InlineData("brk", AddressingMode.Immediate, "a8, i8, native", "8")]
    [InlineData("brk", AddressingMode.Immediate, "a8, i8, emu", "7")]
    [InlineData("rti", AddressingMode.Implied, "a8, i8, native", "7")]

    // Only in emulation mode does a taken branch pay for crossing a page.
    [InlineData("bne", AddressingMode.Relative, "a8, i8, native", "2-3")]
    [InlineData("bne", AddressingMode.Relative, "a8, i8, emu", "2-4")]
    [InlineData("bra", AddressingMode.Relative, "a8, i8, native", "3")]
    public void The65816TakesAsLongAsItsWidthsSay(string mnemonic, AddressingMode mode, string state, string cycles)
    {
        var parts = state.Split(", ");
        var processor = new ProcessorState(Width(parts[0]), Width(parts[1]), parts[2] switch
        {
            "native" => ProcessorMode.Native,
            "emu" => ProcessorMode.Emulation,
            _ => ProcessorMode.Unknown,
        });

        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65816, mnemonic, mode, processor)?.Count.ToString());

        static Width Width(string item) => item[1..] switch
        {
            "8" => Norristown.Semantics.Width.Eight,
            "16" => Norristown.Semantics.Width.Sixteen,
            _ => Norristown.Semantics.Width.Unknown,
        };
    }

    /// <summary>
    /// A direct operand costs one more cycle when the low byte of D is not zero, and may
    /// where D is not known.
    /// </summary>
    [Theory]
    [InlineData("unchanged", "3-4")]
    [InlineData("$2100", "3")]
    [InlineData("$2180", "4")]
    public void ADirectOperandCostsMoreWhereTheLowByteOfDIsNotZero(string page, string cycles)
    {
        var d = page == "unchanged"
            ? StateValue.Unchanged
            : StateValue.Of(Convert.ToInt64(page[1..], 16));
        // This is about D, so the widths are given rather than left to the default, which is `a*`.
        var processor = ProcessorState.Default with { A = Width.Eight, Index = Width.Eight, D = d };

        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65816, "lda", AddressingMode.Direct, processor)?.Count.ToString());
    }

    /// <summary>
    /// A block move takes seven cycles for every byte it moves, and how many that is is in A
    /// when it runs, so nt65 gives it no count.
    /// </summary>
    [Fact]
    public void ABlockMoveHasNoCount()
    {
        Assert.Null(Cycles.Of(Cpu.Wdc65816, "mvn", AddressingMode.BlockMove, ProcessorState.Default));
    }

    /// <summary>The count layout keeps is the one for the mode it chose.</summary>
    [Fact]
    public void LayoutKeepsTheCountForTheModeItChose()
    {
        var analysis = Analysis.Program(("main.nt65", """
            .module main
            .segment ZEROPAGE
            .data near:   .byte

            .segment CODE
            .data far:    .byte 0

            .proc p: a8, i8 {
                lda near
                lda far,x
                rts
            }
            """));
        var layout = analysis.Layouts.Single();
        var lines = analysis.File("main.nt65").Tree.Root.DescendantNodes()
            .OfType<Norristown.Syntax.LineSyntax>()
            .Select(line => line.Statement)
            .OfType<Norristown.Syntax.InstructionStatementSyntax>()
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
        var analysis = Analysis.Program(("main.nt65", ".module main\n.proc p: a8, i8 {\n    ldx #0\n    inx\n    rts\n}\n"));
        var block = analysis.Flows.Single().Regions.Single().Blocks.Single();

        Assert.Equal("10", block.Cycles?.ToString());
    }
}
