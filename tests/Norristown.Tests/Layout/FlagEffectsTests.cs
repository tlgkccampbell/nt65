using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// Checks which flags an instruction writes and reads, for the cases the walk that finds where an
/// input was set depends on. The flags are datasheet facts, so the cases are the ones that depend
/// on the operand or that write the whole status register.
/// </summary>
public sealed class FlagEffectsTests
{
    private const StatusFlags NZ = StatusFlags.Negative | StatusFlags.Zero;

    /// <summary>
    /// <c>plp</c> and <c>rti</c> write every flag. <c>bit</c> copies N and V from memory, except
    /// as an immediate, which tests only Z.
    /// </summary>
    [Theory]
    [InlineData(MnemonicKind.Plp, AddressingMode.Implied, 0xff)]
    [InlineData(MnemonicKind.Rti, AddressingMode.Implied, 0xff)]
    [InlineData(MnemonicKind.Bit, AddressingMode.Absolute, (int)(NZ | StatusFlags.Overflow))]
    [InlineData(MnemonicKind.Bit, AddressingMode.Immediate, (int)StatusFlags.Zero)]
    [InlineData(MnemonicKind.Lda, AddressingMode.Immediate, (int)NZ)]
    [InlineData(MnemonicKind.Sta, AddressingMode.Absolute, 0)]
    public void AnInstructionWritesTheFlagsTheDatasheetLists(MnemonicKind mnemonic, AddressingMode mode, int flags)
    {
        Assert.Equal((StatusFlags)flags, FlagEffects.Written(mnemonic, mode, null));
    }

    /// <summary>A <c>rep</c> or a <c>sep</c> writes the flags its mask names, and any flag where the mask is not known.</summary>
    [Fact]
    public void ARepOrSepWritesTheFlagsItsMaskNames()
    {
        Assert.Equal(StatusFlags.M, FlagEffects.Written(MnemonicKind.Sep, AddressingMode.Immediate, 0x20));
        Assert.Equal(FlagEffects.All, FlagEffects.Written(MnemonicKind.Rep, AddressingMode.Immediate, null));
    }

    /// <summary>A branch reads the flag it tests.</summary>
    [Theory]
    [InlineData(MnemonicKind.Beq, StatusFlags.Zero)]
    [InlineData(MnemonicKind.Bmi, StatusFlags.Negative)]
    [InlineData(MnemonicKind.Bvs, StatusFlags.Overflow)]
    [InlineData(MnemonicKind.Bcc, StatusFlags.Carry)]
    [InlineData(MnemonicKind.Jne, StatusFlags.Zero)]
    public void ABranchReadsTheFlagItTests(MnemonicKind mnemonic, StatusFlags flag)
    {
        Assert.Equal(flag, FlagEffects.Read(mnemonic));
    }
}
