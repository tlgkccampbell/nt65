using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// The facts about each mnemonic beyond which addressing modes it has: whether it calls,
/// returns, stores, pushes or pulls, and which width sizes its immediate. Every pass built on
/// layout reads this table, so a mnemonic missing from it gives a silently wrong answer rather
/// than a failure: a call not treated as a call, a push that moves nothing. The sets are
/// written out a second time here for the same reason <see cref="RegisterEffectsTests"/>
/// writes its table twice.
/// </summary>
public sealed class InstructionFactsTests
{
    /// <summary>
    /// What each mnemonic does to the path. A software interrupt is not a stop: the handler's
    /// <c>rti</c> comes back to the instruction after <c>brk</c> or <c>cop</c>.
    /// </summary>
    [Fact]
    public void EachTransferIsWhatItDoesToThePath()
    {
        Assert.Equal(
            [
                "bbr0", "bbr1", "bbr2", "bbr3", "bbr4", "bbr5", "bbr6", "bbr7",
                "bbs0", "bbs1", "bbs2", "bbs3", "bbs4", "bbs5", "bbs6", "bbs7",
                "bcc", "bcs", "beq", "bmi", "bne", "bpl", "bvc", "bvs",
                "jcc", "jcs", "jeq", "jmi", "jne", "jpl", "jvc", "jvs",
            ],
            Where(facts => facts.Control == Control.Branches));
        Assert.Equal(["bra", "brl", "jml", "jmp"], Where(facts => facts.Control == Control.Jumps));
        Assert.Equal(["jsl", "jsr"], Where(facts => facts.Control == Control.Calls));
        Assert.Equal(["rti", "rtl", "rts"], Where(facts => facts.Control == Control.Returns));
        Assert.Equal(["jam", "stp"], Where(facts => facts.Control == Control.Stops));
    }

    /// <summary>A store is an instruction that writes the memory its operand names.</summary>
    [Fact]
    public void AStoreWritesWhereItsOperandPoints()
    {
        Assert.Equal(
            [
                "asl", "dcp", "dec", "inc", "isc", "lsr", "rla", "rol", "ror", "rra", "sax", "sha",
                "shx", "shy", "slo", "sre", "sta", "stx", "sty", "stz", "tas", "trb", "tsb",
            ],
            Where(facts => facts.Stores));
    }

    /// <summary>Each push and each pull moves the stack by the size of what it transfers.</summary>
    [Fact]
    public void EachPushAndPullMovesWhatItHolds()
    {
        Assert.Equal(
            ["pea", "pei", "per", "pha", "phb", "phd", "phk", "php", "phx", "phy"],
            Where(f => f.Pushes is not null));
        Assert.Equal(["pla", "plb", "pld", "plp", "plx", "ply"], Where(f => f.Pulls is not null));

        Assert.Equal(PushSize.Accumulator, Instructions.Facts(MnemonicKind.Pha).Pushes);
        Assert.Equal(PushSize.Index, Instructions.Facts(MnemonicKind.Phy).Pushes);
        Assert.Equal(PushSize.OneByte, Instructions.Facts(MnemonicKind.Php).Pushes);
        Assert.Equal(PushSize.TwoBytes, Instructions.Facts(MnemonicKind.Pei).Pushes);

        // `Held` names a register only for those `Registers` tracks: A, X, Y and, through the
        // status byte, the carry. The data bank, the direct page and the program bank are not
        // among them.
        Assert.Equal(["pha", "php", "phx", "phy", "pla", "plp", "plx", "ply"], Where(f => f.Held != Registers.None));
        Assert.Equal(Registers.A, Instructions.Facts(MnemonicKind.Pla).Held);
        Assert.Equal(Registers.C, Instructions.Facts(MnemonicKind.Php).Held);
    }

    /// <summary>
    /// These are exactly the instructions whose immediate operand ca65 sizes from the register
    /// width it has been told.
    /// </summary>
    [Fact]
    public void TheWidthDependentImmediatesAreTheOnesCa65Sizes()
    {
        Assert.Equal(
            ["adc", "and", "bit", "cmp", "eor", "lda", "ora", "sbc"],
            Where(facts => facts.SizedBy == WidthRegister.A));
        Assert.Equal(["cpx", "cpy", "ldx", "ldy"], Where(facts => facts.SizedBy == WidthRegister.Index));
    }

    /// <summary>The mnemonics any CPU has whose facts <paramref name="holds"/> accepts, in order.</summary>
    private static IReadOnlyList<string> Where(Func<InstructionFacts, bool> holds) =>
        [.. SyntaxFacts.Mnemonics
            .Where(mnemonic => holds(Instructions.Facts(mnemonic)))
            .Select(SyntaxFacts.TextOf)
            .Order(StringComparer.Ordinal)];
}
