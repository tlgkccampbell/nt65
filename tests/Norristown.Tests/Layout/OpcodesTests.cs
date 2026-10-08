using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Layout;

/// <summary>
/// Tests that the opcode table and the table of each CPU's instruction forms agree: every form a
/// CPU has is written as a byte that decodes back to it, and every byte that decodes names a form
/// the CPU has.
/// </summary>
public sealed class OpcodesTests
{
    /// <summary>Gets every CPU nt65 knows.</summary>
    public static TheoryData<Cpu> Cpus => [.. Enum.GetValues<Cpu>()];

    [Theory]
    [MemberData(nameof(Cpus))]
    public void EveryFormIsWrittenAsAByteThatDecodesBackToIt(Cpu cpu)
    {
        foreach (var mnemonic in Enum.GetValues<MnemonicKind>())
        {
            foreach (var mode in Instructions.Modes(cpu, mnemonic))
            {
                var opcode = Opcodes.Encode(cpu, mnemonic, mode);
                Assert.True(opcode is not null, $"{mnemonic} {mode} has no opcode on {cpu}");
                Assert.Equal((mnemonic, mode), Opcodes.Decode(cpu, opcode.Value));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cpus))]
    public void EveryByteThatDecodesIsAFormTheCpuHas(Cpu cpu)
    {
        for (var opcode = 0; opcode < 256; opcode++)
        {
            if (Opcodes.Decode(cpu, (byte)opcode) is { } form)
                Assert.True(Instructions.Modes(cpu, form.Mnemonic).Contains(form.Mode), $"${opcode:X2} decodes to {form} on {cpu}");
        }
    }

    /// <summary>
    /// Every one of the 256 bytes runs as something on the NMOS 6502 with its undocumented
    /// opcodes, and the duplicates are written as the byte ca65 chooses.
    /// </summary>
    [Fact]
    public void TheUndocumentedOpcodesCoverEveryByte()
    {
        for (var opcode = 0; opcode < 256; opcode++)
            Assert.NotNull(Opcodes.Decode(Cpu.Mos6502X, (byte)opcode));
        Assert.Equal((MnemonicKind.Nop, AddressingMode.DirectX), Opcodes.Decode(Cpu.Mos6502X, 0x34));
        Assert.Equal((byte)0x14, Opcodes.Encode(Cpu.Mos6502X, MnemonicKind.Nop, AddressingMode.DirectX));
        Assert.Equal((MnemonicKind.Nop, AddressingMode.Implied), Opcodes.Decode(Cpu.Mos6502X, 0x3A));
        Assert.Null(Opcodes.Decode(Cpu.Mos6502, 0x3A));
        Assert.Equal((MnemonicKind.Dec, AddressingMode.Accumulator), Opcodes.Decode(Cpu.Wdc65C02, 0x3A));
    }
}
