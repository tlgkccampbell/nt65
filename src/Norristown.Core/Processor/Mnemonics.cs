using Norristown.Syntax;
using static Norristown.Syntax.MnemonicKind;

namespace Norristown.Processor;

/// <summary>
/// Provides each instruction's name and the processor flags it writes. nt65 does not work out
/// either of these. They are datasheet facts, in WDC's words, kept here because they are needed
/// only beside an instruction the editor is showing. Each name drops the tail that says what the
/// instruction works on, since the line already shows that, so the name is
/// <c>load accumulator</c> rather than <c>load accumulator with memory</c>.
/// <para>
/// The undocumented opcodes of the NMOS 6502 have no datasheet to take a name from, so each is
/// named after the two documented instructions it performs at once. The name of an opcode whose
/// result varies from part to part says so, since that is what a reader most needs to know.
/// </para>
/// </summary>
public static class Mnemonics
{
    /// <summary>
    /// The status register's flags, high bit first, which is the order they are listed in.
    /// <c>M</c> and <c>X</c> are the 65816's width flags, and only <c>rep</c>, <c>sep</c> and
    /// the instructions that write the whole register change them.
    /// </summary>
    private static readonly (int Bit, string Name)[] Status =
    [
        (0x80, "N"), (0x40, "V"), (0x20, "M"), (0x10, "X"),
        (0x08, "D"), (0x04, "I"), (0x02, "Z"), (0x01, "C"),
    ];

    /// <summary>
    /// Returns the name of <paramref name="mnemonic"/>, or null if no supported CPU has it. The
    /// eight forms of each bit instruction share a name, because the bit number is already on
    /// the line.
    /// </summary>
    public static string? Name(MnemonicKind mnemonic) => Bare(mnemonic) switch
    {
        Adc => "add with carry",
        Alr => "and accumulator, then shift right",
        Anc => "and accumulator, then copy the sign into carry",
        And => "and accumulator",
        Ane => "and x, the accumulator and the immediate (unstable)",
        Arr => "and accumulator, then rotate right",
        Asl => "arithmetic shift left",
        Axs => "and x with the accumulator, then subtract into x",
        Bbr0 => "branch on bit reset",
        Bbs0 => "branch on bit set",
        Bcc => "branch on carry clear",
        Bcs => "branch on carry set",
        Beq => "branch on equal",
        Bit => "test memory bits",
        Bmi => "branch on minus",
        Bne => "branch on not equal",
        Bpl => "branch on plus",
        Bra => "branch always",
        Brk => "software break",
        Brl => "branch long",
        Bvc => "branch on overflow clear",
        Bvs => "branch on overflow set",
        Clc => "clear carry",
        Cld => "clear decimal mode",
        Cli => "clear interrupt disable",
        Clv => "clear overflow",
        Cmp => "compare accumulator",
        Cop => "coprocessor enable",
        Cpx => "compare index x",
        Cpy => "compare index y",
        Dcp => "decrement, then compare accumulator",
        Dec => "decrement",
        Dex => "decrement index x",
        Dey => "decrement index y",
        Eor => "exclusive or accumulator",
        Inc => "increment",
        Inx => "increment index x",
        Iny => "increment index y",
        Isc => "increment, then subtract with borrow",
        Jam => "stop the processor",
        Jml => "jump long",
        Jmp => "jump",
        Jsl => "jump to subroutine long",
        Jsr => "jump to subroutine",
        Las => "and the stack pointer, into the accumulator, x and it",
        Lax => "load accumulator and index x",
        Lda => "load accumulator",
        Ldx => "load index x",
        Ldy => "load index y",
        Lsr => "logical shift right",
        Mvn => "move block negative",
        Mvp => "move block positive",
        Nop => "no operation",
        Ora => "or accumulator",
        Pea => "push effective absolute address",
        Pei => "push effective indirect address",
        Per => "push effective pc relative address",
        Pha => "push accumulator",
        Phb => "push data bank",
        Phd => "push direct register",
        Phk => "push program bank",
        Php => "push processor status",
        Phx => "push index x",
        Phy => "push index y",
        Pla => "pull accumulator",
        Plb => "pull data bank",
        Pld => "pull direct register",
        Plp => "pull processor status",
        Plx => "pull index x",
        Ply => "pull index y",
        Rep => "reset status bits",
        Rla => "rotate left, then and accumulator",
        Rmb0 => "reset memory bit",
        Rol => "rotate left",
        Ror => "rotate right",
        Rra => "rotate right, then add with carry",
        Rti => "return from interrupt",
        Rtl => "return from subroutine long",
        Rts => "return from subroutine",
        Sax => "store accumulator and index x",
        Sbc => "subtract with borrow",
        Sec => "set carry",
        Sed => "set decimal mode",
        Sei => "set interrupt disable",
        Sep => "set status bits",
        Sha => "store accumulator, x and the address high byte (unstable)",
        Shx => "store index x and the address high byte (unstable)",
        Shy => "store index y and the address high byte (unstable)",
        Slo => "shift left, then or accumulator",
        Smb0 => "set memory bit",
        Sre => "shift right, then exclusive or accumulator",
        Sta => "store accumulator",
        Stp => "stop the clock",
        Stx => "store index x",
        Sty => "store index y",
        Stz => "store zero",
        Tas => "transfer accumulator and x to the stack pointer, then store (unstable)",
        Tax => "transfer accumulator to x",
        Tay => "transfer accumulator to y",
        Tcd => "transfer c to direct register",
        Tcs => "transfer c to stack pointer",
        Tdc => "transfer direct register to c",
        Trb => "test and reset bits",
        Tsb => "test and set bits",
        Tsc => "transfer stack pointer to c",
        Tsx => "transfer stack pointer to x",
        Txa => "transfer x to accumulator",
        Txs => "transfer x to stack pointer",
        Txy => "transfer x to y",
        Tya => "transfer y to accumulator",
        Tyx => "transfer y to x",
        Wai => "wait for interrupt",
        Wdm => "reserved for expansion",
        Xba => "exchange b and a",
        Xce => "exchange carry and emulation",
        _ => null,
    };

    /// <summary>
    /// Returns the flags <paramref name="mnemonic"/> writes, in the order the status register
    /// holds them, or null if it writes none. An instruction that writes the whole register gets
    /// <c>all</c> rather than a list of every flag. <paramref name="constant"/> is the value of
    /// the immediate when it is known. It lets a <c>rep</c> or a <c>sep</c> get the flags its
    /// mask names instead of <c>all</c>.
    /// </summary>
    public static string? Flags(Cpu cpu, MnemonicKind mnemonic, AddressingMode mode, long? constant)
    {
        if (mnemonic is Rep or Sep)
            return constant is { } mask ? Format(mask) : "all";
        if (mnemonic is Plp or Rti)
            return "all";

        // The 65C02 and the 65816 read an immediate `bit` as a mask test and leave N and V
        // alone; every other form copies the two high bits of what it read.
        if (mnemonic == Bit)
            return mode == AddressingMode.Immediate ? "Z" : "N V Z";

        // The NMOS 6502 leaves the decimal flag unchanged when it takes an interrupt, a trap
        // every CMOS part closed by clearing the flag.
        if (mnemonic is Brk or Cop)
            return cpu == Cpu.Mos6502 ? "I" : "D I";
        return Bare(mnemonic) switch
        {
            Adc or Sbc => "N V Z C",
            Cmp or Cpx or Cpy => "N Z C",
            Asl or Lsr or Rol or Ror => "N Z C",
            And or Eor or Ora or Lda or Ldx or Ldy => "N Z",

            // Each undocumented opcode writes the flags its pair of documented instructions writes.
            Rra or Isc or Arr => "N V Z C",
            Slo or Rla or Sre or Dcp or Alr or Anc or Axs => "N Z C",
            Lax or Las or Ane => "N Z",
            Inc or Dec or Inx or Dex or Iny or Dey => "N Z",
            Pla or Plx or Ply or Plb or Pld => "N Z",
            Tax or Tay or Txa or Tya or Tsx or Txy or Tyx => "N Z",
            Tcd or Tdc or Tsc or Xba => "N Z",
            Trb or Tsb => "Z",
            Clc or Sec or Xce => "C",
            Cld or Sed => "D",
            Cli or Sei => "I",
            Clv => "V",
            _ => null,
        };
    }

    /// <summary>
    /// Returns the mnemonic without the bit number a bit instruction names, since the eight
    /// forms of each are one instruction as far as the name and the flags go.
    /// </summary>
    private static MnemonicKind Bare(MnemonicKind mnemonic) => SyntaxFacts.BitOf(mnemonic)?.Family ?? mnemonic;

    /// <summary>
    /// Formats the flags a <c>rep</c> or <c>sep</c> mask names, in the status register's own
    /// order.
    /// </summary>
    private static string Format(long mask) =>
        string.Join(" ", Status.Where(flag => (mask & flag.Bit) != 0).Select(flag => flag.Name));
}
