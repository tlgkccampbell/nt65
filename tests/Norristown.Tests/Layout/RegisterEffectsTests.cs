using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// Which registers each instruction writes. The table is written out twice, here and in
/// <see cref="RegisterEffects"/>, because a mnemonic left out of it would not fail anything
/// on its own: it would quietly say the instruction writes nothing, and a routine would
/// promise to hand back a register it had destroyed.
/// </summary>
public sealed class RegisterEffectsTests
{
    /// <summary>
    /// What each mnemonic writes, where that does not depend on the operand. The four whose
    /// answer does — the shifts, `inc`, `dec`, `rep` and `sep` — are asked about below.
    /// </summary>
    private static readonly Dictionary<string, Registers> Writes = Table();

    /// <summary>
    /// Every mnemonic any CPU has is in the table. A new instruction fails this until someone
    /// says what it does to the registers, which is the whole point of writing it out twice.
    /// </summary>
    [Fact]
    public void EveryMnemonicIsInTheTable()
    {
        Assert.Equal(
            SyntaxFacts.Mnemonics.Order(StringComparer.Ordinal),
            Writes.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Each mnemonic writes what the table says it writes.</summary>
    [Fact]
    public void EachMnemonicWritesWhatTheTableSays()
    {
        foreach (var (mnemonic, written) in Writes)
            Assert.Equal((mnemonic, written), (mnemonic, RegisterEffects.Written(mnemonic, AddressingMode.Implied, null)));
    }

    /// <summary>A shift through the accumulator writes it; one through memory writes only the carry.</summary>
    [Theory]
    [InlineData("asl")]
    [InlineData("lsr")]
    [InlineData("rol")]
    [InlineData("ror")]
    public void AShiftWritesTheAccumulatorOnlyWhenItGoesThroughIt(string mnemonic)
    {
        Assert.Equal(Registers.A | Registers.C, RegisterEffects.Written(mnemonic, AddressingMode.Accumulator, null));
        Assert.Equal(Registers.C, RegisterEffects.Written(mnemonic, AddressingMode.Absolute, null));
    }

    /// <summary>The CMOS `inc a` and `dec a` write the accumulator; through memory they write nothing.</summary>
    [Theory]
    [InlineData("inc")]
    [InlineData("dec")]
    public void AnIncrementWritesTheAccumulatorOnlyWhenItGoesThroughIt(string mnemonic)
    {
        Assert.Equal(Registers.A, RegisterEffects.Written(mnemonic, AddressingMode.Accumulator, null));
        Assert.Equal(Registers.None, RegisterEffects.Written(mnemonic, AddressingMode.Direct, null));
    }

    /// <summary>
    /// Bit 0 of a `rep` or `sep` is the carry, so one that does not name it leaves it alone. An
    /// operand nt65 cannot work out may name it.
    /// </summary>
    [Theory]
    [InlineData("rep")]
    [InlineData("sep")]
    public void RepAndSepWriteTheCarryOnlyWhenTheyNameIt(string mnemonic)
    {
        Assert.Equal(Registers.None, RegisterEffects.Written(mnemonic, AddressingMode.Immediate, 0x30));
        Assert.Equal(Registers.C, RegisterEffects.Written(mnemonic, AddressingMode.Immediate, 0x31));
        Assert.Equal(Registers.C, RegisterEffects.Written(mnemonic, AddressingMode.Immediate, null));
    }

    /// <summary>The transfers between the three registers a value is held in carry the value over.</summary>
    [Fact]
    public void TheTransfersBetweenTheValueRegistersAreMoves()
    {
        Assert.Equal((Registers.A, Registers.X), RegisterEffects.Moved("tax"));
        Assert.Equal((Registers.Y, Registers.A), RegisterEffects.Moved("tya"));
        Assert.Equal((Registers.X, Registers.Y), RegisterEffects.Moved("txy"));

        // The stack pointer and the 65816's D are not registers this follows, so what comes
        // out of them is a value like any other.
        Assert.Null(RegisterEffects.Moved("tsx"));
        Assert.Null(RegisterEffects.Moved("txs"));
        Assert.Null(RegisterEffects.Moved("tdc"));
        Assert.Null(RegisterEffects.Moved("xba"));
    }

    /// <summary>What every mnemonic writes, with an implied or absolute operand.</summary>
    private static Dictionary<string, Registers> Table()
    {
        var table = new Dictionary<string, Registers>(StringComparer.Ordinal);
        Add(Registers.A, "lda", "pla", "txa", "tya", "tdc", "tsc", "xba", "and", "ora", "eor");
        Add(Registers.A | Registers.C, "adc", "sbc");
        Add(Registers.X, "ldx", "plx", "tax", "tsx", "tyx", "inx", "dex");
        Add(Registers.Y, "ldy", "ply", "tay", "txy", "iny", "dey");
        Add(Registers.C, "cmp", "cpx", "cpy", "clc", "sec", "plp", "rti", "asl", "lsr", "rol", "ror", "rep", "sep");
        Add(Registers.A | Registers.X | Registers.Y, "mvn", "mvp");
        Add(Registers.All, "xce", "brk", "cop");

        // Everything else leaves all four alone: the stores, the pushes, the branches, the
        // jumps and returns, the flags that are not the carry, and the bit instructions.
        Add(
            Registers.None,
            "sta", "stx", "sty", "stz", "bit", "tsb", "trb", "inc", "dec", "nop", "wdm", "wai", "stp",
            "cld", "sed", "cli", "sei", "clv", "txs", "tcd", "tcs",
            "pha", "phx", "phy", "php", "phb", "phd", "phk", "pea", "pei", "per", "plb", "pld",
            "jmp", "jml", "jsr", "jsl", "rts", "rtl", "bra", "brl",
            "bcc", "bcs", "beq", "bne", "bmi", "bpl", "bvc", "bvs",
            "jcc", "jcs", "jeq", "jne", "jmi", "jpl", "jvc", "jvs");
        for (var bit = 0; bit < 8; bit++)
            Add(Registers.None, $"bbr{bit}", $"bbs{bit}", $"rmb{bit}", $"smb{bit}");
        return table;

        void Add(Registers written, params string[] mnemonics)
        {
            foreach (var mnemonic in mnemonics)
                table.Add(mnemonic, written);
        }
    }
}
