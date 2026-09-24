using Norristown.Layout;
using Norristown.Processor;
using Norristown.Syntax;
using Norristown.Semantics;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Layout;

/// <summary>
/// Checks how long each instruction takes. The count is an interval wherever it depends on
/// something the program does not specify, such as whether an indexed read crosses a page,
/// whether a branch is taken, and on the 65C02 whether the decimal flag is set.
/// <para>
/// The ca65 oracle checks instruction lengths, not timings, so the reference values below are
/// the only check on nt65's cycle table.
/// </para>
/// </summary>
public sealed class CycleTests
{
    [Theory]
    // The 6502, by addressing mode.
    [InlineData(MnemonicKind.Lda, AddressingMode.Immediate, "2")]
    [InlineData(MnemonicKind.Lda, AddressingMode.Direct, "3")]
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectX, "4")]
    [InlineData(MnemonicKind.Lda, AddressingMode.Absolute, "4")]
    [InlineData(MnemonicKind.Ldx, AddressingMode.DirectY, "4")]
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectIndirectX, "6")]

    // An indexed read pays for a page crossing only when it crosses one. A store always pays,
    // because it cannot begin until the address is complete.
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteX, "4-5")]
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteY, "4-5")]
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectIndirectY, "5-6")]
    [InlineData(MnemonicKind.Sta, AddressingMode.AbsoluteX, "5")]
    [InlineData(MnemonicKind.Sta, AddressingMode.AbsoluteY, "5")]
    [InlineData(MnemonicKind.Sta, AddressingMode.DirectIndirectY, "6")]

    // A read-modify-write has a fixed count, and the indexed form always pays the cycle that a
    // read pays only on a page crossing.
    [InlineData(MnemonicKind.Asl, AddressingMode.Accumulator, "2")]
    [InlineData(MnemonicKind.Asl, AddressingMode.Direct, "5")]
    [InlineData(MnemonicKind.Asl, AddressingMode.DirectX, "6")]
    [InlineData(MnemonicKind.Asl, AddressingMode.Absolute, "6")]
    [InlineData(MnemonicKind.Asl, AddressingMode.AbsoluteX, "7")]
    [InlineData(MnemonicKind.Inc, AddressingMode.Absolute, "6")]

    [InlineData(MnemonicKind.Inx, AddressingMode.Implied, "2")]
    [InlineData(MnemonicKind.Pha, AddressingMode.Implied, "3")]
    [InlineData(MnemonicKind.Php, AddressingMode.Implied, "3")]
    [InlineData(MnemonicKind.Pla, AddressingMode.Implied, "4")]
    [InlineData(MnemonicKind.Plp, AddressingMode.Implied, "4")]
    [InlineData(MnemonicKind.Rts, AddressingMode.Implied, "6")]
    [InlineData(MnemonicKind.Rti, AddressingMode.Implied, "6")]
    [InlineData(MnemonicKind.Brk, AddressingMode.Immediate, "7")]
    [InlineData(MnemonicKind.Jmp, AddressingMode.Absolute, "3")]
    [InlineData(MnemonicKind.Jsr, AddressingMode.Absolute, "6")]

    // The 6502's indirect jump reads its pointer without carrying into the high byte.
    [InlineData(MnemonicKind.Jmp, AddressingMode.AbsoluteIndirect, "5")]

    // Two not taken, three taken, and one more when a taken branch crosses a page.
    [InlineData(MnemonicKind.Beq, AddressingMode.Relative, "2-4")]
    [InlineData(MnemonicKind.Bcc, AddressingMode.Relative, "2-4")]
    public void The6502TakesTheCyclesItsTableLists(MnemonicKind mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Mos6502, mnemonic, mode)?.Count.ToString());
    }

    [Theory]
    // The 6502's own counts, unchanged.
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteX, "4-5")]
    [InlineData(MnemonicKind.Jmp, AddressingMode.AbsoluteIndirect, "5")]
    [InlineData(MnemonicKind.Nop, AddressingMode.Implied, "2")]

    // An undocumented read-modify-write combined with an arithmetic operation costs what the
    // read-modify-write alone costs, and its indexed forms always pay the index cycle.
    [InlineData(MnemonicKind.Slo, AddressingMode.Direct, "5")]
    [InlineData(MnemonicKind.Slo, AddressingMode.DirectX, "6")]
    [InlineData(MnemonicKind.Rla, AddressingMode.Absolute, "6")]
    [InlineData(MnemonicKind.Sre, AddressingMode.AbsoluteX, "7")]
    [InlineData(MnemonicKind.Rra, AddressingMode.AbsoluteY, "7")]
    [InlineData(MnemonicKind.Dcp, AddressingMode.DirectIndirectX, "8")]
    [InlineData(MnemonicKind.Isc, AddressingMode.DirectIndirectY, "8")]

    // A load pays for a page crossing only when it crosses one, as the documented loads do.
    [InlineData(MnemonicKind.Lax, AddressingMode.Immediate, "2")]
    [InlineData(MnemonicKind.Lax, AddressingMode.Direct, "3")]
    [InlineData(MnemonicKind.Lax, AddressingMode.DirectY, "4")]
    [InlineData(MnemonicKind.Lax, AddressingMode.AbsoluteY, "4-5")]
    [InlineData(MnemonicKind.Lax, AddressingMode.DirectIndirectY, "5-6")]
    [InlineData(MnemonicKind.Las, AddressingMode.AbsoluteY, "4-5")]
    [InlineData(MnemonicKind.Sax, AddressingMode.Absolute, "4")]
    [InlineData(MnemonicKind.Sax, AddressingMode.DirectIndirectX, "6")]

    // The immediate-only opcodes, and the indexed stores, which always pay the page-crossing
    // cycle because they complete the address before writing.
    [InlineData(MnemonicKind.Alr, AddressingMode.Immediate, "2")]
    [InlineData(MnemonicKind.Axs, AddressingMode.Immediate, "2")]
    [InlineData(MnemonicKind.Sha, AddressingMode.AbsoluteY, "5")]
    [InlineData(MnemonicKind.Sha, AddressingMode.DirectIndirectY, "6")]
    [InlineData(MnemonicKind.Shx, AddressingMode.AbsoluteY, "5")]
    [InlineData(MnemonicKind.Tas, AddressingMode.AbsoluteY, "5")]

    // The undocumented `nop` forms read an operand, and the indexed form pays for a page
    // crossing only when it crosses one, like any other read.
    [InlineData(MnemonicKind.Nop, AddressingMode.Direct, "3")]
    [InlineData(MnemonicKind.Nop, AddressingMode.AbsoluteX, "4-5")]
    public void The6502xTakesTheCyclesItsTableLists(MnemonicKind mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Mos6502X, mnemonic, mode)?.Count.ToString());
    }

    /// <summary>
    /// <c>jam</c> halts the processor, so it has no cycle count. The block containing it
    /// reports why it has no count rather than quietly leaving the instruction out.
    /// </summary>
    [Fact]
    public void JamHasNoCount() => Assert.Null(Cycles.Of(Cpu.Mos6502X, MnemonicKind.Jam, AddressingMode.Implied));

    [Theory]
    // What the 65C02 keeps.
    [InlineData(MnemonicKind.Lda, AddressingMode.Direct, "3")]
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteX, "4-5")]
    [InlineData(MnemonicKind.Asl, AddressingMode.Direct, "5")]

    // What it adds.
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectIndirect, "5")]
    [InlineData(MnemonicKind.Sta, AddressingMode.DirectIndirect, "5")]
    [InlineData(MnemonicKind.Bra, AddressingMode.Relative, "3-4")]
    [InlineData(MnemonicKind.Phx, AddressingMode.Implied, "3")]
    [InlineData(MnemonicKind.Ply, AddressingMode.Implied, "4")]
    [InlineData(MnemonicKind.Stz, AddressingMode.Direct, "3")]
    [InlineData(MnemonicKind.Stz, AddressingMode.AbsoluteX, "5")]
    [InlineData(MnemonicKind.Trb, AddressingMode.Direct, "5")]
    [InlineData(MnemonicKind.Tsb, AddressingMode.Absolute, "6")]
    [InlineData(MnemonicKind.Inc, AddressingMode.Accumulator, "2")]
    [InlineData(MnemonicKind.Bit, AddressingMode.Immediate, "2")]
    [InlineData(MnemonicKind.Rmb3, AddressingMode.Direct, "5")]
    [InlineData(MnemonicKind.Bbr0, AddressingMode.DirectRelative, "5-7")]
    [InlineData(MnemonicKind.Stp, AddressingMode.Implied, "3")]

    // What it fixes: the indirect jump costs a cycle and reads the pointer properly, and a
    // shift indexed absolutely pays for a page crossing only when it crosses one.
    [InlineData(MnemonicKind.Jmp, AddressingMode.AbsoluteIndirect, "6")]
    [InlineData(MnemonicKind.Jmp, AddressingMode.AbsoluteIndirectX, "6")]
    [InlineData(MnemonicKind.Asl, AddressingMode.AbsoluteX, "6-7")]
    [InlineData(MnemonicKind.Inc, AddressingMode.AbsoluteX, "7")]

    // Decimal arithmetic costs one more, and nothing in the program says whether the
    // decimal flag is set where the instruction runs.
    [InlineData(MnemonicKind.Adc, AddressingMode.Immediate, "2-3")]
    [InlineData(MnemonicKind.Sbc, AddressingMode.Absolute, "4-5")]
    [InlineData(MnemonicKind.Adc, AddressingMode.AbsoluteX, "4-6")]
    [InlineData(MnemonicKind.And, AddressingMode.Immediate, "2")]
    public void The65C02TakesTheCyclesItsTableLists(MnemonicKind mnemonic, AddressingMode mode, string cycles)
    {
        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65C02, mnemonic, mode)?.Count.ToString());
    }

    [Theory]
    // The 8-bit forms, in native mode, match the 65C02's apart from decimal arithmetic,
    // which costs nothing more on the 65816.
    [InlineData(MnemonicKind.Lda, AddressingMode.Immediate, "a8, i8, native", "2")]
    [InlineData(MnemonicKind.Adc, AddressingMode.Immediate, "a8, i8, native", "2")]
    [InlineData(MnemonicKind.Lda, AddressingMode.Absolute, "a8, i8, native", "4")]

    // A 16-bit register reads and writes a byte more, and a read-modify-write two.
    [InlineData(MnemonicKind.Lda, AddressingMode.Immediate, "a16, i8, native", "3")]
    [InlineData(MnemonicKind.Lda, AddressingMode.Absolute, "a16, i8, native", "5")]
    [InlineData(MnemonicKind.Sta, AddressingMode.Absolute, "a16, i8, native", "5")]
    [InlineData(MnemonicKind.Asl, AddressingMode.Absolute, "a16, i8, native", "8")]
    [InlineData(MnemonicKind.Asl, AddressingMode.Accumulator, "a16, i8, native", "2")]
    [InlineData(MnemonicKind.Ldx, AddressingMode.Immediate, "a16, i8, native", "2")]
    [InlineData(MnemonicKind.Ldx, AddressingMode.Immediate, "a8, i16, native", "3")]

    // An unknown width gives an interval covering both widths.
    [InlineData(MnemonicKind.Lda, AddressingMode.Immediate, "a?, i8, native", "2-3")]

    // An indexed read pays for crossing a page only sometimes, unless the index is 16 bits,
    // when it always does.
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteX, "a8, i8, native", "4-5")]
    [InlineData(MnemonicKind.Lda, AddressingMode.AbsoluteX, "a8, i16, native", "5")]
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectIndirectY, "a8, i16, native", "6-7")]

    // A direct operand costs one more when D's low byte is not zero, which is not known.
    [InlineData(MnemonicKind.Lda, AddressingMode.Direct, "a8, i8, native", "3-4")]

    // What only the 65816 has.
    [InlineData(MnemonicKind.Lda, AddressingMode.Long, "a8, i8, native", "5")]
    [InlineData(MnemonicKind.Lda, AddressingMode.LongX, "a16, i8, native", "6")]
    [InlineData(MnemonicKind.Lda, AddressingMode.StackRelative, "a8, i8, native", "4")]
    [InlineData(MnemonicKind.Lda, AddressingMode.StackRelativeIndirectY, "a8, i8, native", "7")]
    [InlineData(MnemonicKind.Lda, AddressingMode.DirectIndirectLong, "a8, i8, native", "6-7")]
    [InlineData(MnemonicKind.Jsl, AddressingMode.Long, "a8, i8, native", "8")]
    [InlineData(MnemonicKind.Jml, AddressingMode.AbsoluteIndirectLong, "a8, i8, native", "6")]
    [InlineData(MnemonicKind.Jsr, AddressingMode.AbsoluteIndirectX, "a8, i8, native", "8")]
    [InlineData(MnemonicKind.Rtl, AddressingMode.Implied, "a8, i8, native", "6")]
    [InlineData(MnemonicKind.Brl, AddressingMode.RelativeLong, "a8, i8, native", "4")]
    [InlineData(MnemonicKind.Per, AddressingMode.RelativeLong, "a8, i8, native", "6")]
    [InlineData(MnemonicKind.Pea, AddressingMode.Absolute, "a8, i8, native", "5")]
    [InlineData(MnemonicKind.Rep, AddressingMode.Immediate, "a8, i8, native", "3")]
    [InlineData(MnemonicKind.Xba, AddressingMode.Implied, "a8, i8, native", "3")]
    [InlineData(MnemonicKind.Pha, AddressingMode.Implied, "a16, i8, native", "4")]
    [InlineData(MnemonicKind.Phd, AddressingMode.Implied, "a8, i8, native", "4")]
    [InlineData(MnemonicKind.Pld, AddressingMode.Implied, "a8, i8, native", "5")]

    // Native mode pushes and pulls the program bank as well.
    [InlineData(MnemonicKind.Brk, AddressingMode.Immediate, "a8, i8, native", "8")]
    [InlineData(MnemonicKind.Brk, AddressingMode.Immediate, "a8, i8, emu", "7")]
    [InlineData(MnemonicKind.Rti, AddressingMode.Implied, "a8, i8, native", "7")]

    // Only in emulation mode does a taken branch pay for crossing a page.
    [InlineData(MnemonicKind.Bne, AddressingMode.Relative, "a8, i8, native", "2-3")]
    [InlineData(MnemonicKind.Bne, AddressingMode.Relative, "a8, i8, emu", "2-4")]
    [InlineData(MnemonicKind.Bra, AddressingMode.Relative, "a8, i8, native", "3")]
    public void The65816TakesTheCyclesItsWidthsImply(MnemonicKind mnemonic, AddressingMode mode, string state, string cycles)
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
    /// A direct operand costs one more cycle when the low byte of D is not zero, and may cost
    /// one more when D is not known.
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
        // This test is about D, so the widths are fixed at 8 bits rather than left at the
        // default, which leaves them unchanged (`a*`) and so not a single known width.
        var processor = ProcessorState.Default with { A = Width.Eight, Index = Width.Eight, D = d };

        Assert.Equal(cycles, Cycles.Of(Cpu.Wdc65816, MnemonicKind.Lda, AddressingMode.Direct, processor)?.Count.ToString());
    }

    /// <summary>
    /// A block move takes seven cycles for every byte it moves, and the number of bytes is
    /// whatever A holds at run time, so nt65 gives it no count.
    /// </summary>
    [Fact]
    public void ABlockMoveHasNoCount()
    {
        Assert.Null(Cycles.Of(Cpu.Wdc65816, MnemonicKind.Mvn, AddressingMode.BlockMove, ProcessorState.Default));
    }

    /// <summary>
    /// The cycle count layout records for an instruction is the one for the addressing mode
    /// layout chose for it, which is direct for a zero-page operand and absolute for any other.
    /// </summary>
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
        var layout = analysis.Files.Single().Layout;
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
    /// Long, it is the inverted branch, which falls through into a <c>jmp</c> when the original
    /// condition holds.
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
        var block = analysis.Files.Single().Flow.Regions.Single().Blocks.Single();

        Assert.Equal("10", block.Cycles?.ToString());
    }
}
