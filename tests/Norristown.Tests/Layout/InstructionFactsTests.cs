using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// What a mnemonic is, beyond which modes it has. Every pass above layout reads this table,
/// so a mnemonic missing from it says the quiet wrong thing rather than failing: a call that
/// does not call, a push that moves nothing. The sets are written out a second time here for
/// the same reason <see cref="RegisterEffectsTests"/> writes its table twice.
/// </summary>
public sealed class InstructionFactsTests
{
    /// <summary>Only <c>jsr</c> and <c>jsl</c> call, and only the three returns return.</summary>
    [Fact]
    public void OnlyACallCallsAndOnlyAReturnReturns()
    {
        Assert.Equal(["jsl", "jsr"], Where(facts => facts.Calls));
        Assert.Equal(["rti", "rtl", "rts"], Where(facts => facts.Returns));
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

    /// <summary>Each push moves as much as the register it holds, and each pull the same.</summary>
    [Fact]
    public void EachPushAndPullMovesWhatItHolds()
    {
        Assert.Equal(
            ["pea", "pei", "per", "pha", "phb", "phd", "phk", "php", "phx", "phy"],
            Where(f => f.Pushes is not null));
        Assert.Equal(["pla", "plb", "pld", "plp", "plx", "ply"], Where(f => f.Pulls is not null));

        Assert.Equal(PushSize.Accumulator, Instructions.Facts("pha").Pushes);
        Assert.Equal(PushSize.Index, Instructions.Facts("phy").Pushes);
        Assert.Equal(PushSize.OneByte, Instructions.Facts("php").Pushes);
        Assert.Equal(PushSize.TwoBytes, Instructions.Facts("pei").Pushes);

        // What a push holds is a register only where a pull could give it back to one: the
        // data bank, the direct page and the program bank are not among them.
        Assert.Equal(["pha", "php", "phx", "phy", "pla", "plp", "plx", "ply"], Where(f => f.Held != Registers.None));
        Assert.Equal(Registers.A, Instructions.Facts("pla").Held);
        Assert.Equal(Registers.C, Instructions.Facts("php").Held);
    }

    /// <summary>ca65 sizes exactly these immediates from the width it is told.</summary>
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
            .Order(StringComparer.Ordinal)];
}
