using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents one instruction the bytes from a <c>.label</c> inside an instruction run as, on the
/// way to the start of an instruction as written. One <see cref="HiddenPath"/> holds them in the
/// order they run.
/// </summary>
/// <param name="Mnemonic">The instruction's mnemonic.</param>
/// <param name="Mode">Its addressing mode.</param>
/// <param name="Operand">The value of its operand, from the bytes after its opcode, or 0 where it has none.</param>
/// <param name="Cycles">How many cycles it takes, or null where nt65 has no count for it.</param>
public sealed record HiddenInstruction(MnemonicKind Mnemonic, AddressingMode Mode, long Operand, CycleCount? Cycles)
{
    /// <summary>Returns the instruction as it would be written, such as <c>asl $91</c>.</summary>
    public override string ToString()
    {
        var name = SyntaxFacts.TextOf(Mnemonic);
        var at = Instructions.Width(Mode) is AddressSize.ZeroPage ? $"${Operand:X2}" : $"${Operand:X4}";
        return Mode switch
        {
            AddressingMode.Implied => name,
            AddressingMode.Accumulator => $"{name} a",
            AddressingMode.Immediate => $"{name} #${Operand:X2}",
            AddressingMode.Direct or AddressingMode.Absolute => $"{name} {at}",
            AddressingMode.DirectX or AddressingMode.AbsoluteX => $"{name} {at},x",
            AddressingMode.DirectY or AddressingMode.AbsoluteY => $"{name} {at},y",
            AddressingMode.DirectIndirect or AddressingMode.AbsoluteIndirect => $"{name} ({at})",
            AddressingMode.DirectIndirectX or AddressingMode.AbsoluteIndirectX => $"{name} ({at},x)",
            AddressingMode.DirectIndirectY => $"{name} ({at}),y",
            AddressingMode.Long => $"{name} f:${Operand:X6}",
            AddressingMode.LongX => $"{name} f:${Operand:X6},x",
            AddressingMode.DirectIndirectLong => $"{name} [{at}]",
            AddressingMode.DirectIndirectLongY => $"{name} [{at}],y",
            AddressingMode.StackRelative => $"{name} ${Operand:X2},s",
            AddressingMode.StackRelativeIndirectY => $"{name} (${Operand:X2},s),y",
            _ => $"{name} ${Operand:X}",
        };
    }
}
