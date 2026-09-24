using System.Collections.Frozen;

namespace Norristown.Processor;

/// <summary>
/// Lists the words ca65 reads as instructions under the <c>.setcpu</c> that nt65 writes for
/// each CPU.
/// <para>
/// These lists are not nt65's instruction set, and they are not the words a program may use.
/// ca65 keeps alternative spellings that nt65 does not have, such as <c>swa</c>, <c>tad</c>,
/// <c>dea</c> and <c>ina</c>. It also reads any word in its table at the start of a line as an
/// instruction, so a label with that name, written without a prefix, would be read as one. The
/// emitter handles this by writing such a name with its module in front.
/// </para>
/// <para>
/// The words are kept here because they are a fact about the assembler rather than about the
/// processor. A test reads ca65's own tables from the pinned ca65 source and checks these
/// lists against them.
/// </para>
/// </summary>
public static class Ca65Instructions
{
    // Static field initializers run in text order, so the word lists come before the sets
    // built from them.

    // ca65's table for the 6502, which every later table in it starts from.
    private static readonly string[] Mos6502 =
    [
        "adc", "and", "asl", "bcc", "bcs", "beq", "bit", "bmi", "bne", "bpl", "brk", "bvc", "bvs",
        "clc", "cld", "cli", "clv", "cmp", "cpx", "cpy", "dec", "dex", "dey", "eor", "inc", "inx",
        "iny", "jmp", "jsr", "lda", "ldx", "ldy", "lsr", "nop", "ora", "pha", "php", "pla", "plp",
        "rol", "ror", "rti", "rts", "sbc", "sec", "sed", "sei", "sta", "stx", "sty", "tax", "tay",
        "tsx", "txa", "txs", "tya",
    ];

    // The NMOS 6502's undocumented opcodes, which ca65 reads only under `6502X`.
    private static readonly string[] Undocumented =
    [
        "alr", "anc", "ane", "arr", "axs", "dcp", "isc", "jam", "las", "lax", "rla", "rra", "sax",
        "sha", "shx", "shy", "slo", "sre", "tas",
    ];

    // The instructions the CMOS parts add, including `dea` and `ina`, which are `dec a` and
    // `inc a` in nt65.
    private static readonly string[] Cmos = ["bra", "dea", "ina", "phx", "phy", "plx", "ply", "stz", "trb", "tsb"];

    // Rockwell's bit instructions, each in the eight forms ca65's table lists one by one.
    private static readonly string[] Bits =
        [.. from name in new[] { "bbr", "bbs", "rmb", "smb" } from bit in Enumerable.Range(0, 8) select $"{name}{bit}"];

    // WDC's two instructions that stop the clock.
    private static readonly string[] Wdc = ["stp", "wai"];

    // The 65816's own instructions, with the alternative spellings ca65 keeps for six of its
    // transfers.
    private static readonly string[] Wdc65816 =
    [
        "brl", "cop", "cpa", "jml", "jsl", "mvn", "mvp", "pea", "pei", "per", "phb", "phd", "phk",
        "plb", "pld", "rep", "rtl", "sep", "swa", "tad", "tas", "tcd", "tcs", "tda", "tdc", "tsa",
        "tsc", "txy", "tyx", "wdm", "xba", "xce",
    ];

    private static readonly FrozenSet<string> mos6502 = Words(Mos6502);

    private static readonly FrozenSet<string> mos6502X = Words([.. Mos6502, .. Undocumented]);

    private static readonly FrozenSet<string> cmos65SC02 = Words([.. Mos6502, .. Cmos]);

    private static readonly FrozenSet<string> rockwell65C02 = Words([.. Mos6502, .. Cmos, .. Bits]);

    private static readonly FrozenSet<string> wdc65C02 = Words([.. Mos6502, .. Cmos, .. Bits, .. Wdc]);

    private static readonly FrozenSet<string> wdc65816 = Words([.. Mos6502, .. Cmos, .. Wdc, .. Wdc65816]);

    private static readonly FrozenSet<string> anywhere =
        Words([.. Mos6502, .. Undocumented, .. Cmos, .. Bits, .. Wdc, .. Wdc65816]);

    /// <summary>
    /// Returns the words ca65 reads as instructions under the <c>.setcpu</c> that nt65 writes for
    /// <paramref name="cpu"/>.
    /// </summary>
    public static IReadOnlySet<string> Of(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => mos6502,
        Cpu.Mos6502X => mos6502X,
        Cpu.Cmos65SC02 => cmos65SC02,
        Cpu.Rockwell65C02 => rockwell65C02,
        Cpu.Wdc65C02 => wdc65C02,
        _ => wdc65816,
    };

    /// <summary>
    /// Returns a value indicating whether ca65 would read <paramref name="name"/> as an
    /// instruction on <paramref name="cpu"/>.
    /// </summary>
    public static bool Has(Cpu cpu, string name) => Of(cpu).Contains(name);

    /// <summary>
    /// Returns a value indicating whether ca65 would read <paramref name="name"/> as an
    /// instruction under any <c>.setcpu</c> that nt65 writes. A name given to the linker has to
    /// be definable under every one of them, because whether ca65 can define it must not depend
    /// on which CPU a module is built for.
    /// </summary>
    public static bool HasAnywhere(string name) => anywhere.Contains(name);

    private static FrozenSet<string> Words(string[] words) => words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
