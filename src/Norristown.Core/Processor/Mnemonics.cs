namespace Norristown.Processor;

/// <summary>
/// What each instruction is called, and which of the processor's flags it writes. Neither is
/// anything nt65 works out: they are datasheet facts, in WDC's words, kept here because the
/// only place they are needed is beside an instruction the editor is showing. The names are
/// trimmed of the tail that says what the instruction works on, since the line already says
/// that: <c>load accumulator</c> rather than <c>load accumulator with memory</c>.
/// <para>
/// The undocumented opcodes of the NMOS 6502 have no datasheet to take a name from, so each is
/// named after the two documented instructions it performs at once; the ones whose result
/// varies from part to part say so, since that is what a reader most needs to know about them.
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
    /// What <paramref name="mnemonic"/> is called, or null for one no supported CPU has. The
    /// eight forms of each bit instruction share a name, because the bit number is already on the line.
    /// </summary>
    public static string? Name(string mnemonic) => Bare(mnemonic) switch
    {
        "adc" => "add with carry",
        "alr" => "and accumulator, then shift right",
        "anc" => "and accumulator, then copy the sign into carry",
        "and" => "and accumulator",
        "ane" => "and x, the accumulator and the immediate (unstable)",
        "arr" => "and accumulator, then rotate right",
        "asl" => "arithmetic shift left",
        "axs" => "and x with the accumulator, then subtract into x",
        "bbr" => "branch on bit reset",
        "bbs" => "branch on bit set",
        "bcc" => "branch on carry clear",
        "bcs" => "branch on carry set",
        "beq" => "branch on equal",
        "bit" => "test memory bits",
        "bmi" => "branch on minus",
        "bne" => "branch on not equal",
        "bpl" => "branch on plus",
        "bra" => "branch always",
        "brk" => "software break",
        "brl" => "branch long",
        "bvc" => "branch on overflow clear",
        "bvs" => "branch on overflow set",
        "clc" => "clear carry",
        "cld" => "clear decimal mode",
        "cli" => "clear interrupt disable",
        "clv" => "clear overflow",
        "cmp" => "compare accumulator",
        "cop" => "coprocessor enable",
        "cpx" => "compare index x",
        "cpy" => "compare index y",
        "dcp" => "decrement, then compare accumulator",
        "dec" => "decrement",
        "dex" => "decrement index x",
        "dey" => "decrement index y",
        "eor" => "exclusive or accumulator",
        "inc" => "increment",
        "inx" => "increment index x",
        "iny" => "increment index y",
        "isc" => "increment, then subtract with borrow",
        "jam" => "stop the processor",
        "jml" => "jump long",
        "jmp" => "jump",
        "jsl" => "jump to subroutine long",
        "jsr" => "jump to subroutine",
        "las" => "and the stack pointer, into the accumulator, x and it",
        "lax" => "load accumulator and index x",
        "lda" => "load accumulator",
        "ldx" => "load index x",
        "ldy" => "load index y",
        "lsr" => "logical shift right",
        "mvn" => "move block negative",
        "mvp" => "move block positive",
        "nop" => "no operation",
        "ora" => "or accumulator",
        "pea" => "push effective absolute address",
        "pei" => "push effective indirect address",
        "per" => "push effective pc relative address",
        "pha" => "push accumulator",
        "phb" => "push data bank",
        "phd" => "push direct register",
        "phk" => "push program bank",
        "php" => "push processor status",
        "phx" => "push index x",
        "phy" => "push index y",
        "pla" => "pull accumulator",
        "plb" => "pull data bank",
        "pld" => "pull direct register",
        "plp" => "pull processor status",
        "plx" => "pull index x",
        "ply" => "pull index y",
        "rep" => "reset status bits",
        "rla" => "rotate left, then and accumulator",
        "rmb" => "reset memory bit",
        "rol" => "rotate left",
        "ror" => "rotate right",
        "rra" => "rotate right, then add with carry",
        "rti" => "return from interrupt",
        "rtl" => "return from subroutine long",
        "rts" => "return from subroutine",
        "sax" => "store accumulator and index x",
        "sbc" => "subtract with borrow",
        "sec" => "set carry",
        "sed" => "set decimal mode",
        "sei" => "set interrupt disable",
        "sep" => "set status bits",
        "sha" => "store accumulator, x and the address high byte (unstable)",
        "shx" => "store index x and the address high byte (unstable)",
        "shy" => "store index y and the address high byte (unstable)",
        "slo" => "shift left, then or accumulator",
        "smb" => "set memory bit",
        "sre" => "shift right, then exclusive or accumulator",
        "sta" => "store accumulator",
        "stp" => "stop the clock",
        "stx" => "store index x",
        "sty" => "store index y",
        "stz" => "store zero",
        "tas" => "transfer accumulator and x to the stack pointer, then store (unstable)",
        "tax" => "transfer accumulator to x",
        "tay" => "transfer accumulator to y",
        "tcd" => "transfer c to direct register",
        "tcs" => "transfer c to stack pointer",
        "tdc" => "transfer direct register to c",
        "trb" => "test and reset bits",
        "tsb" => "test and set bits",
        "tsc" => "transfer stack pointer to c",
        "tsx" => "transfer stack pointer to x",
        "txa" => "transfer x to accumulator",
        "txs" => "transfer x to stack pointer",
        "txy" => "transfer x to y",
        "tya" => "transfer y to accumulator",
        "tyx" => "transfer y to x",
        "wai" => "wait for interrupt",
        "wdm" => "reserved for expansion",
        "xba" => "exchange b and a",
        "xce" => "exchange carry and emulation",
        _ => null,
    };

    /// <summary>
    /// The flags <paramref name="mnemonic"/> writes, in the order the status register holds
    /// them, or null where it writes none. An instruction that writes the register whole is
    /// <c>all</c> rather than a list of every flag there is; <paramref name="constant"/> is
    /// the value of the immediate when it is known, which is what turns a <c>rep</c> or a
    /// <c>sep</c> from <c>all</c> into the flags its mask names.
    /// </summary>
    public static string? Flags(Cpu cpu, string mnemonic, AddressingMode mode, long? constant)
    {
        var name = mnemonic.ToLowerInvariant();
        if (name is "rep" or "sep")
            return constant is { } mask ? Spell(mask) : "all";
        if (name is "plp" or "rti")
            return "all";

        // The 65C02 and the 65816 read an immediate `bit` as a mask test and leave N and V
        // alone; every other form copies the two high bits of what it read.
        if (name == "bit")
            return mode == AddressingMode.Immediate ? "Z" : "N V Z";

        // The NMOS 6502 leaves the decimal flag unchanged when it takes an interrupt, a trap
        // every CMOS part closed by clearing the flag.
        if (name is "brk" or "cop")
            return cpu == Cpu.Mos6502 ? "I" : "D I";
        return Bare(name) switch
        {
            "adc" or "sbc" => "N V Z C",
            "cmp" or "cpx" or "cpy" => "N Z C",
            "asl" or "lsr" or "rol" or "ror" => "N Z C",
            "and" or "eor" or "ora" or "lda" or "ldx" or "ldy" => "N Z",

            // Each undocumented opcode writes the flags its pair of documented instructions writes.
            "rra" or "isc" or "arr" => "N V Z C",
            "slo" or "rla" or "sre" or "dcp" or "alr" or "anc" or "axs" => "N Z C",
            "lax" or "las" or "ane" => "N Z",
            "inc" or "dec" or "inx" or "dex" or "iny" or "dey" => "N Z",
            "pla" or "plx" or "ply" or "plb" or "pld" => "N Z",
            "tax" or "tay" or "txa" or "tya" or "tsx" or "txy" or "tyx" => "N Z",
            "tcd" or "tdc" or "tsc" or "xba" => "N Z",
            "trb" or "tsb" => "Z",
            "clc" or "sec" or "xce" => "C",
            "cld" or "sed" => "D",
            "cli" or "sei" => "I",
            "clv" => "V",
            _ => null,
        };
    }

    /// <summary>
    /// The mnemonic without the bit a bit instruction names, since the eight forms of each are
    /// one instruction as far as its name and its flags go.
    /// </summary>
    private static string Bare(string mnemonic)
    {
        var name = mnemonic.ToLowerInvariant();
        return name.Length == 4 && name[3] is >= '0' and <= '7' && name[..3] is "bbr" or "bbs" or "rmb" or "smb"
            ? name[..3]
            : name;
    }

    /// <summary>The flags a <c>rep</c> or <c>sep</c> mask names, in the register's own order.</summary>
    private static string Spell(long mask) =>
        string.Join(" ", Status.Where(flag => (mask & flag.Bit) != 0).Select(flag => flag.Name));
}
