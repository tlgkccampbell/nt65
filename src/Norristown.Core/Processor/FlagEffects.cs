using Norristown.Syntax;
using static Norristown.Syntax.MnemonicKind;

namespace Norristown.Processor;

/// <summary>
/// Determines which processor flags each instruction writes and which it reads. These are
/// facts about the processors, not anything nt65 works out. <see cref="Mnemonics.Flags"/> shows
/// the flags an instruction writes beside it, and the walk that finds where an input's value was
/// set follows them.
/// <para>
/// <see cref="RegisterEffects"/> takes the C, Z, N and V flags it follows as
/// <see cref="Registers"/> from this table.
/// </para>
/// </summary>
public static class FlagEffects
{
    /// <summary>Gets every flag of the status register.</summary>
    public static StatusFlags All =>
        StatusFlags.Carry | StatusFlags.Zero | StatusFlags.InterruptDisable | StatusFlags.Decimal
        | StatusFlags.X | StatusFlags.M | StatusFlags.Overflow | StatusFlags.Negative;

    /// <summary>
    /// Returns the flags <paramref name="mnemonic"/> writes in <paramref name="mode"/>.
    /// <paramref name="constant"/> is the value of an immediate operand when it is known, which
    /// gives a <c>rep</c> or a <c>sep</c> the flags its mask names. Without it, either may write
    /// any flag. A software interrupt clears the decimal flag, as every CMOS part does, though the
    /// NMOS 6502 leaves it alone.
    /// </summary>
    public static StatusFlags Written(MnemonicKind mnemonic, AddressingMode? mode, long? constant)
    {
        const StatusFlags NZ = StatusFlags.Negative | StatusFlags.Zero;
        const StatusFlags NZC = NZ | StatusFlags.Carry;
        const StatusFlags NVZC = NZC | StatusFlags.Overflow;
        if (mnemonic is Rep or Sep)
            return constant is { } mask ? (StatusFlags)(mask & 0xff) : All;
        if (mnemonic is Plp or Rti)
            return All;

        // The 65C02 and the 65816 read an immediate `bit` as a mask test and leave N and V alone;
        // every other form copies the two high bits of what it read.
        if (mnemonic == Bit)
            return mode == AddressingMode.Immediate ? StatusFlags.Zero : NZ | StatusFlags.Overflow;
        return (SyntaxFacts.BitOf(mnemonic)?.Group ?? mnemonic) switch
        {
            Brk or Cop => StatusFlags.Decimal | StatusFlags.InterruptDisable,
            Adc or Sbc => NVZC,
            Cmp or Cpx or Cpy => NZC,
            Asl or Lsr or Rol or Ror => NZC,
            And or Eor or Ora or Lda or Ldx or Ldy => NZ,

            // Each undocumented opcode writes the flags its pair of documented instructions writes.
            Rra or Isc or Arr => NVZC,
            Slo or Rla or Sre or Dcp or Alr or Anc or Axs => NZC,
            Lax or Las or Ane => NZ,
            Inc or Dec or Inx or Dex or Iny or Dey => NZ,
            Pla or Plx or Ply or Plb or Pld => NZ,
            Tax or Tay or Txa or Tya or Tsx or Txy or Tyx => NZ,
            Tcd or Tdc or Tsc or Xba => NZ,
            Trb or Tsb => StatusFlags.Zero,
            Clc or Sec or Xce => StatusFlags.Carry,
            Cld or Sed => StatusFlags.Decimal,
            Cli or Sei => StatusFlags.InterruptDisable,
            Clv => StatusFlags.Overflow,
            _ => StatusFlags.None,
        };
    }

    /// <summary>
    /// Returns the flags <paramref name="mnemonic"/> uses the value of. A branch reads the flag it
    /// tests, and an add, a subtract or a rotate reads the carry. A push of the status register is
    /// not counted, because what it saves is only used where something takes it back.
    /// </summary>
    public static StatusFlags Read(MnemonicKind mnemonic) => mnemonic switch
    {
        Bcc or Bcs or Jcc or Jcs => StatusFlags.Carry,
        Beq or Bne or Jeq or Jne => StatusFlags.Zero,
        Bmi or Bpl or Jmi or Jpl => StatusFlags.Negative,
        Bvc or Bvs or Jvc or Jvs => StatusFlags.Overflow,
        Adc or Sbc => StatusFlags.Carry | StatusFlags.Decimal,
        Rol or Ror or Rla or Rra or Arr or Xce => StatusFlags.Carry,
        Isc => StatusFlags.Carry | StatusFlags.Decimal,
        _ => StatusFlags.None,
    };
}
