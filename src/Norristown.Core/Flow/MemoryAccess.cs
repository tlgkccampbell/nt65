using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents how one instruction reaches memory through its operand, as the memory part of
/// <see cref="InputSources"/> follows it. Only direct addressing names a <see cref="Location"/>.
/// An indexed operand names the location it starts from, and an indirect one names nothing,
/// because either may reach a location other than the one it names. An indirect operand does read
/// its pointer directly, though, so the bytes of the pointer are reads of their own. An operand
/// that a <c>.patch</c> says the program rewrites is taken as indexed, because the location written
/// in the source is only where the program starts from.
/// </summary>
/// <param name="Direct">The location a direct operand names, or null.</param>
/// <param name="Reads">Whether the instruction reads the memory it reaches.</param>
/// <param name="Stores">Whether the instruction writes the memory it reaches, including by a read-modify-write.</param>
/// <param name="Indexed">The location an indexed operand starts from, or null.</param>
/// <param name="Indirect">Whether the operand reaches memory through a pointer.</param>
/// <param name="Pointer">The bytes of the pointer an indirect operand reads, where it names one directly.</param>
internal readonly record struct MemoryAccess(
    Location? Direct, bool Reads, bool Stores, Location? Indexed, bool Indirect, ImmutableArray<Location> Pointer)
{
    /// <summary>
    /// Returns how the instruction at <paramref name="step"/> in <paramref name="file"/> reaches
    /// memory, or null where it neither reads nor writes memory through its operand.
    /// </summary>
    public static MemoryAccess? Of(FileAnalysis file, Step step)
    {
        var (model, layout) = (file.Model, file.Layout);
        if (step.Statement is not InstructionStatementSyntax statement)
            return null;
        var mnemonic = statement.MnemonicKind;
        var reads = ReadsMemory(mnemonic);
        var stores = Instructions.Facts(mnemonic).Stores;
        var mode = layout.Of(statement, step.On)?.Mode;
        if ((!reads && !stores) || mode is null
            or AddressingMode.Implied or AddressingMode.Accumulator or AddressingMode.Immediate
            or AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.DirectRelative
            or AddressingMode.BlockMove or AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
        {
            return null;
        }

        var operand = StepOperands.Of(model, step);
        var location = operand is null ? null : Location.Of(model, CodeLayout.Expression(operand), step.On);
        if (file.Flow.Patched.Contains(step.Key))
            return new MemoryAccess(null, reads, stores, location, false, []);
        return mode switch
        {
            AddressingMode.Direct or AddressingMode.Absolute or AddressingMode.Long
                => new MemoryAccess(location, reads, stores, null, false, []),
            AddressingMode.DirectX or AddressingMode.DirectY or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
                or AddressingMode.LongX => new MemoryAccess(null, reads, stores, location, false, []),
            _ => new MemoryAccess(null, reads, stores, null, true, Bytes(location, mode)),
        };
    }

    /// <summary>
    /// Returns the bytes of the pointer an indirect operand reads directly. A pointer that X indexes
    /// is not read directly, and a long pointer is three bytes.
    /// </summary>
    private static ImmutableArray<Location> Bytes(Location? pointer, AddressingMode? mode)
    {
        if (pointer is not { } start || mode is AddressingMode.DirectIndirectX or AddressingMode.AbsoluteIndirectX)
            return [];
        var length = mode is AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY
            or AddressingMode.AbsoluteIndirectLong ? 3 : 2;
        return [.. Enumerable.Range(0, length).Select(i => start with { Offset = start.Offset + i })];
    }

    /// <summary>
    /// Returns whether <paramref name="mnemonic"/> reads the memory its operand reaches. A
    /// read-modify-write reads it before it writes it.
    /// </summary>
    internal static bool ReadsMemory(MnemonicKind mnemonic) => mnemonic is
        MnemonicKind.Lda or MnemonicKind.Ldx or MnemonicKind.Ldy or MnemonicKind.Adc or MnemonicKind.Sbc
        or MnemonicKind.And or MnemonicKind.Ora or MnemonicKind.Eor or MnemonicKind.Cmp or MnemonicKind.Cpx
        or MnemonicKind.Cpy or MnemonicKind.Bit or MnemonicKind.Lax or MnemonicKind.Las or MnemonicKind.Inc
        or MnemonicKind.Dec or MnemonicKind.Asl or MnemonicKind.Lsr or MnemonicKind.Rol or MnemonicKind.Ror
        or MnemonicKind.Trb or MnemonicKind.Tsb or MnemonicKind.Slo or MnemonicKind.Rla or MnemonicKind.Sre
        or MnemonicKind.Rra or MnemonicKind.Dcp or MnemonicKind.Isc;
}
