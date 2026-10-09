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
/// <param name="Width">
/// How many bytes the access is sure to reach in memory, from where it lands. That is 2 on the
/// 65816 where the register that sizes the access is known to be 16 bits, and 1 otherwise.
/// </param>
/// <param name="Wider">
/// Whether the access may reach one byte more than <paramref name="Width"/> says, because on the
/// 65816 the width of the register that sizes it is not known.
/// </param>
/// <param name="Reads">Whether the instruction reads the memory it reaches.</param>
/// <param name="Stores">Whether the instruction writes the memory it reaches, including by a read-modify-write.</param>
/// <param name="Indexed">The location an indexed operand starts from, or null.</param>
/// <param name="Indirect">Whether the operand reaches memory through a pointer.</param>
/// <param name="Pointer">The bytes of the pointer an indirect operand reads, where it names one directly.</param>
/// <param name="Index">
/// The constant the index register of an indexed operand holds, where the instructions on every
/// path give it one, or null.
/// </param>
/// <param name="Reach">
/// The largest value the index register of an indexed operand can hold, which is $FF for an 8-bit
/// index and $FFFF for a 16-bit one or one whose width is not known.
/// </param>
internal readonly record struct MemoryAccess(
    Location? Direct, int Width, bool Wider, bool Reads, bool Stores, Location? Indexed, bool Indirect, ImmutableArray<Location> Pointer,
    long? Index = null, long Reach = 0xff)
{
    /// <summary>
    /// Gets each byte a direct operand reaches, from the location it names on, or nothing where the
    /// operand is not direct. A 16-bit access to <c>ptr</c> reaches <c>ptr</c> and <c>ptr+1</c>.
    /// </summary>
    public ImmutableArray<Location> DirectBytes => Direct is { } start
        ? [.. Enumerable.Range(0, Width).Select(i => start with { Offset = start.Offset + i })]
        : [];

    /// <summary>
    /// Gets each byte a direct operand reaches or may reach. That is <see cref="DirectBytes"/> and,
    /// where the access is <see cref="Wider"/>, the byte after them.
    /// </summary>
    public ImmutableArray<Location> ReachedBytes => Wider && Direct is { } start
        ? DirectBytes.Add(start with { Offset = start.Offset + Width })
        : DirectBytes;

    /// <summary>Gets the indexed store this access makes, or null where it makes none.</summary>
    public IndexedStore? IndexedStore => Stores && Indexed is { } start ? new IndexedStore(start, Index, Reach) : null;

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

        // On the 65816 an access is as wide as the register that sizes it. While that width is
        // not known, the access is sure of one byte and may reach the next one too.
        var processor = layout.Cpu == Cpu.Wdc65816 ? file.State?.Before(step.Statement, step.On)?.Processor : null;
        var sized = layout.Cpu == Cpu.Wdc65816 && Instructions.MemorySizedBy(mnemonic) is { } register
            ? processor?.Of(register) ?? Semantics.Width.Unknown
            : Semantics.Width.Eight;
        var width = sized == Semantics.Width.Sixteen ? 2 : 1;
        var wider = sized is not (Semantics.Width.Eight or Semantics.Width.Sixteen);
        var operand = StepOperands.Of(model, step);
        var location = operand is null ? null : Location.Of(model, CodeLayout.Expression(operand), step.On);
        if (file.Flow.Patched.Contains(step.Key))
            return new MemoryAccess(null, width, wider, reads, stores, location, false, [], null, 0xffff);

        // An index register's constant narrows where an indexed store lands to one byte. Without
        // one, the store may land anywhere the register can reach.
        var known = file.Flow.KnownBefore(statement);
        var reach = layout.Cpu == Cpu.Wdc65816 && processor?.Index != Semantics.Width.Eight ? 0xffff : 0xff;
        return mode switch
        {
            AddressingMode.Direct or AddressingMode.Absolute or AddressingMode.Long
                => new MemoryAccess(location, width, wider, reads, stores, null, false, []),
            AddressingMode.DirectX or AddressingMode.AbsoluteX or AddressingMode.LongX
                => new MemoryAccess(null, width, wider, reads, stores, location, false, [], known?.X, reach),
            AddressingMode.DirectY or AddressingMode.AbsoluteY
                => new MemoryAccess(null, width, wider, reads, stores, location, false, [], known?.Y, reach),
            _ => new MemoryAccess(null, width, wider, reads, stores, null, true, Bytes(location, mode)),
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
