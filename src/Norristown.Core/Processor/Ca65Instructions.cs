using System.Collections.Frozen;

namespace Norristown.Processor;

/// <summary>
/// The words ca65 reads as an instruction under the <c>.setcpu</c> nt65 writes for each CPU.
/// <para>
/// This is not nt65's instruction set and is not what a program may write. ca65 keeps
/// alternative spellings nt65 does not have — <c>swa</c>, <c>tad</c>, <c>dea</c>, <c>ina</c> —
/// and takes any word in its table at the start of a line for an instruction, so a label
/// with that name, written without a prefix, would be read as one. Handling that is the
/// emitter's job: it writes such a name with its module in front. The words are kept here
/// because they are a fact about the assembler rather than about the processor, and a test
/// reads ca65's own tables from the pinned ca65 source and checks these lists against them.
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

    // What the CMOS parts add, `dea` and `ina` among them, which nt65 spells `dec a` and `inc a`.
    private static readonly string[] Cmos = ["bra", "dea", "ina", "phx", "phy", "plx", "ply", "stz", "trb", "tsb"];

    // Rockwell's bit instructions, each in the eight forms ca65's table lists one by one.
    private static readonly string[] Bits =
        [.. from name in new[] { "bbr", "bbs", "rmb", "smb" } from bit in Enumerable.Range(0, 8) select $"{name}{bit}"];

    // WDC's two that stop the clock.
    private static readonly string[] Wdc = ["stp", "wai"];

    // The 65816's own, with the alternative spellings ca65 keeps for six of its transfers.
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

    /// <summary>The words ca65 has under the <c>.setcpu</c> nt65 writes for <paramref name="cpu"/>.</summary>
    public static IReadOnlySet<string> Of(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => mos6502,
        Cpu.Mos6502X => mos6502X,
        Cpu.Cmos65SC02 => cmos65SC02,
        Cpu.Rockwell65C02 => rockwell65C02,
        Cpu.Wdc65C02 => wdc65C02,
        _ => wdc65816,
    };

    /// <summary>Whether ca65 would read <paramref name="name"/> as an instruction on <paramref name="cpu"/>.</summary>
    public static bool Has(Cpu cpu, string name) => Of(cpu).Contains(name);

    /// <summary>
    /// Whether ca65 would read <paramref name="name"/> as an instruction under any
    /// <c>.setcpu</c> nt65 writes. A name given to the linker has to be definable under every
    /// one of them: whether ca65 can define it must not depend on which CPU a module is built for.
    /// </summary>
    public static bool HasAnywhere(string name) => anywhere.Contains(name);

    private static FrozenSet<string> Words(string[] words) => words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
