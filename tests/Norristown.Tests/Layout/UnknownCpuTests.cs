using Norristown.Layout;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// Checks that every table chosen by CPU throws for a value outside <see cref="Cpu"/>, rather
/// than quietly answering from the last CPU's table.
/// </summary>
public sealed class UnknownCpuTests
{
    private const Cpu Unknown = (Cpu)99;

    [Fact]
    public void EveryTableRejectsAnUnknownCpu()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Instructions.Modes(Unknown, MnemonicKind.Lda));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cycles.Of(Unknown, MnemonicKind.Lda, AddressingMode.Immediate));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ca65Instructions.Of(Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuNames.Format(Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuNames.FormatForCa65(Unknown));
    }
}
