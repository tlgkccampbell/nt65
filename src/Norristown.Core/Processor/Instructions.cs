using System.Collections.Frozen;
using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>
/// Which addressing modes each mnemonic has, on each CPU, and how long each one is. Syntax
/// does not depend on the CPU, so every form parses everywhere and this table is what says
/// whether the target actually has it.
/// </summary>
public static class Instructions
{
    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> mos6502 = Build6502();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> mos6502X = Build6502X();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> cmos65SC02 = Build65SC02();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> rockwell65C02 = BuildRockwell();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> wdc65C02 = Build65C02();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> wdc65816 = Build65816();

    private static readonly FrozenDictionary<string, InstructionFacts> facts = BuildFacts();

    /// <summary>
    /// What <paramref name="mnemonic"/> is, beyond which modes it has: whether it calls or
    /// returns, what it pushes or pulls, and which registers it leaves changed.
    /// </summary>
    public static InstructionFacts Facts(string mnemonic) =>
        facts.GetValueOrDefault(mnemonic.ToLowerInvariant(), InstructionFacts.None);

    /// <summary>Whether <paramref name="cpu"/> has <paramref name="mnemonic"/> at all.</summary>
    public static bool Has(Cpu cpu, string mnemonic) => Modes(cpu, mnemonic).Count > 0;

    /// <summary>
    /// Whether a program built for <paramref name="cpu"/> may write <paramref name="mnemonic"/>:
    /// the CPU's own instructions, and the long branches, which nt65 writes on every CPU.
    /// </summary>
    public static bool Writable(Cpu cpu, string mnemonic) =>
        Has(cpu, mnemonic) || SyntaxFacts.LongBranches.Contains(mnemonic);

    /// <summary>The modes <paramref name="mnemonic"/> has on <paramref name="cpu"/>, empty if it has none.</summary>
    public static IReadOnlySet<AddressingMode> Modes(Cpu cpu, string mnemonic)
    {
        var table = cpu switch
        {
            Cpu.Mos6502 => mos6502,
            Cpu.Mos6502X => mos6502X,
            Cpu.Cmos65SC02 => cmos65SC02,
            Cpu.Rockwell65C02 => rockwell65C02,
            Cpu.Wdc65C02 => wdc65C02,
            _ => wdc65816,
        };
        return table.GetValueOrDefault(mnemonic.ToLowerInvariant(), FrozenSet<AddressingMode>.Empty);
    }

    /// <summary>
    /// How many bytes an instruction in <paramref name="mode"/> takes: the opcode and its
    /// operand. An immediate is one byte here; on the 65816 the immediate of an instruction
    /// <see cref="SizedBy"/> names a register for is as wide as that register.
    /// </summary>
    public static int Length(AddressingMode mode) => mode switch
    {
        AddressingMode.Implied or AddressingMode.Accumulator => 1,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX
            or AddressingMode.AbsoluteIndirectLong or AddressingMode.DirectRelative
            or AddressingMode.RelativeLong or AddressingMode.BlockMove => 3,
        AddressingMode.Long or AddressingMode.LongX => 4,
        _ => 2,
    };

    /// <summary>
    /// The register whose width sizes <paramref name="mnemonic"/>'s immediate on the 65816,
    /// or null when its immediate is always one byte.
    /// </summary>
    public static WidthRegister? SizedBy(string mnemonic) => Facts(mnemonic).SizedBy;

    /// <summary>
    /// How wide the address in an operand of this mode is, or null where the mode carries no
    /// address to size: one byte for the direct page, two for absolute and three for long.
    /// </summary>
    public static AddressSize? Width(AddressingMode mode) => mode switch
    {
        AddressingMode.Direct or AddressingMode.DirectX or AddressingMode.DirectY
            or AddressingMode.DirectIndirect or AddressingMode.DirectIndirectX
            or AddressingMode.DirectIndirectY or AddressingMode.DirectRelative
            or AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY => AddressSize.ZeroPage,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX
            or AddressingMode.AbsoluteIndirectLong => AddressSize.Absolute,
        AddressingMode.Long or AddressingMode.LongX => AddressSize.Far,
        _ => null,
    };

    /// <summary>The <c>z:</c>, <c>a:</c> or <c>f:</c> that makes a mode's width explicit.</summary>
    public static string? Prefix(AddressingMode mode) => Width(mode) switch
    {
        AddressSize.ZeroPage => "z:",
        AddressSize.Absolute => "a:",
        AddressSize.Far => "f:",
        _ => null,
    };

    /// <summary>
    /// Whether a mnemonic's operand names a place to reach rather than an address to size, so
    /// what it takes is a near or a far target: every jump, call and branch, and <c>per</c>,
    /// which reaches its target the way <c>brl</c> does and pushes it. Which names those are is
    /// the same question whatever the program is built for, so every CPU nt65 knows is asked.
    /// </summary>
    public static bool IsControlTransfer(string mnemonic) =>
        CpuNames.All.Any(cpu => Modes(cpu, mnemonic).Any(mode =>
            mode is AddressingMode.Relative or AddressingMode.DirectRelative or AddressingMode.RelativeLong))
        || SyntaxFacts.LongBranches.Contains(mnemonic)
        || mnemonic.ToLowerInvariant() is "jmp" or "jsr" or "jml" or "jsl";

    /// <summary>
    /// The two short branches a long branch is written with: the one it takes when the
    /// target is in reach, and its opposite, which skips the <c>jmp</c> when it is not.
    /// </summary>
    public static (string Taken, string Skipped) FormsOf(string mnemonic)
    {
        var condition = mnemonic.ToLowerInvariant()[1..];
        var opposite = condition switch
        {
            "eq" => "ne",
            "ne" => "eq",
            "cs" => "cc",
            "cc" => "cs",
            "mi" => "pl",
            "pl" => "mi",
            "vs" => "vc",
            _ => "vs",
        };
        return ("b" + condition, "b" + opposite);
    }

    /// <summary>
    /// What each mnemonic is. The groups read the way the passes above ask about them: what
    /// writes which register, what moves one to another, what calls, returns and stores, what
    /// the stack instructions move, and what the 65816 sizes by a width.
    /// </summary>
    private static FrozenDictionary<string, InstructionFacts> BuildFacts()
    {
        var table = new Dictionary<string, InstructionFacts>(StringComparer.Ordinal);

        // A shift or an increment through the accumulator writes it and one through memory
        // does not, and which flags a `rep` or a `sep` names is in its operand; this is the
        // widest each of them can write, and the mode and the operand narrow it.
        Fact(table, "lda pla txa tya tdc tsc xba and ora eor", f => f with { Writes = Registers.A });
        Fact(table, "adc sbc asl lsr rol ror", f => f with { Writes = Registers.A | Registers.C });
        Fact(table, "inc dec", f => f with { Writes = Registers.A });
        Fact(table, "ldx plx tax tsx tyx inx dex", f => f with { Writes = Registers.X });
        Fact(table, "ldy ply tay txy iny dey", f => f with { Writes = Registers.Y });
        Fact(table, "cmp cpx cpy clc sec plp rti rep sep", f => f with { Writes = Registers.C });

        // A block move counts down in A and walks X and Y along the two banks. Swapping the
        // carry with the emulation flag truncates the index registers and hides half the
        // accumulator, and a software interrupt runs a handler this program may not even hold.
        Fact(table, "mvn mvp", f => f with { Writes = Registers.A | Registers.X | Registers.Y });
        Fact(table, "xce brk cop", f => f with { Writes = Registers.All });

        Fact(table, "tax", f => f with { Copies = (Registers.A, Registers.X) });
        Fact(table, "tay", f => f with { Copies = (Registers.A, Registers.Y) });
        Fact(table, "txa", f => f with { Copies = (Registers.X, Registers.A) });
        Fact(table, "tya", f => f with { Copies = (Registers.Y, Registers.A) });
        Fact(table, "txy", f => f with { Copies = (Registers.X, Registers.Y) });
        Fact(table, "tyx", f => f with { Copies = (Registers.Y, Registers.X) });

        Fact(table, "jsr jsl", f => f with { Calls = true });
        Fact(table, "rts rtl rti", f => f with { Returns = true });
        Fact(table, "sta stx sty stz inc dec asl lsr rol ror tsb trb", f => f with { Stores = true });

        Fact(table, "pha", f => f with { Pushes = PushSize.Accumulator, Held = Registers.A });
        Fact(table, "phx", f => f with { Pushes = PushSize.Index, Held = Registers.X });
        Fact(table, "phy", f => f with { Pushes = PushSize.Index, Held = Registers.Y });
        Fact(table, "php", f => f with { Pushes = PushSize.OneByte, Held = Registers.C });
        Fact(table, "phb phk", f => f with { Pushes = PushSize.OneByte });
        Fact(table, "phd pea pei per", f => f with { Pushes = PushSize.TwoBytes });
        Fact(table, "pla", f => f with { Pulls = PushSize.Accumulator, Held = Registers.A });
        Fact(table, "plx", f => f with { Pulls = PushSize.Index, Held = Registers.X });
        Fact(table, "ply", f => f with { Pulls = PushSize.Index, Held = Registers.Y });
        Fact(table, "plp", f => f with { Pulls = PushSize.OneByte, Held = Registers.C });
        Fact(table, "plb", f => f with { Pulls = PushSize.OneByte });
        Fact(table, "pld", f => f with { Pulls = PushSize.TwoBytes });

        Fact(table, "lda adc and bit cmp eor ora sbc", f => f with { SizedBy = WidthRegister.A });
        Fact(table, "ldx ldy cpx cpy", f => f with { SizedBy = WidthRegister.Index });

        // The undocumented opcodes of the NMOS 6502, which only the 6502x has. Each is two of
        // the documented instructions happening at once, so what it writes is what both write.
        Fact(table, "slo rla sre alr anc arr", f => f with { Writes = Registers.A | Registers.C });
        Fact(table, "rra isc", f => f with { Writes = Registers.A | Registers.C });
        Fact(table, "dcp", f => f with { Writes = Registers.C });
        Fact(table, "axs", f => f with { Writes = Registers.X | Registers.C });
        Fact(table, "lax las", f => f with { Writes = Registers.A | Registers.X });
        Fact(table, "ane", f => f with { Writes = Registers.A });
        Fact(table, "slo rla sre rra dcp isc sax sha shx shy tas", f => f with { Stores = true });

        // Nothing after `jam` runs, so what it leaves is no question anyone gets to ask.
        Fact(table, "jam", f => f with { Writes = Registers.All });
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Fact(
        Dictionary<string, InstructionFacts> table, string mnemonics, Func<InstructionFacts, InstructionFacts> with)
    {
        foreach (var mnemonic in mnemonics.Split(' '))
            table[mnemonic] = with(table.GetValueOrDefault(mnemonic, InstructionFacts.None));
    }

    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build6502()
    {
        var table = new Dictionary<string, HashSet<AddressingMode>>(StringComparer.Ordinal);
        Add(table, "adc and cmp eor lda ora sbc",
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX, AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX,
            AddressingMode.DirectIndirectY);
        Add(table, "sta",
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, "asl lsr rol ror",
            AddressingMode.Accumulator, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        Add(table, "inc dec",
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX);
        Add(table, "ldx",
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute,
            AddressingMode.AbsoluteY);
        Add(table, "ldy",
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        Add(table, "stx", AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute);
        Add(table, "sty", AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute);
        Add(table, "cpx cpy", AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.Absolute);
        Add(table, "bit", AddressingMode.Direct, AddressingMode.Absolute);
        Add(table, "jmp", AddressingMode.Absolute, AddressingMode.AbsoluteIndirect);
        Add(table, "jsr", AddressingMode.Absolute);
        Add(table, "bcc bcs beq bmi bne bpl bvc bvs", AddressingMode.Relative);

        // `brk` takes a signature byte on every CPU, and is two bytes wide.
        Add(table, "brk", AddressingMode.Immediate);
        Add(table,
            "clc cld cli clv dex dey inx iny nop pha php pla plp rti rts sec sed sei tax tay tsx txa txs tya",
            AddressingMode.Implied);
        return Freeze(table);
    }

    /// <summary>
    /// The NMOS 6502's undocumented opcodes, in ca65's spellings and its forms, which is what
    /// the output has to assemble as. They are not a processor's set: no datasheet lists them,
    /// and which of them a part runs the same way is a fact about the silicon. What nt65 takes
    /// from ca65 is the names, the modes and the encodings; what each does, and what it costs,
    /// is read from the NMOS behaviour and is left out where it is not one answer.
    /// <para>
    /// The documented set also grows one instruction: <c>nop</c> takes the operands its
    /// undocumented encodings read, so <c>nop $12</c> and <c>nop abs,x</c> are instructions here
    /// and nowhere else.
    /// </para>
    /// </summary>
    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build6502X()
    {
        var table = Copy(mos6502);

        // The read-modify-write pairs, each an official instruction folded into another: they
        // take every mode the store they are built on takes.
        Add(table, "slo rla sre rra dcp isc",
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, "lax",
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, "sax",
            AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute, AddressingMode.DirectIndirectX);

        // The immediate-only ones, which pass A through an operation and the carry or the flags.
        Add(table, "alr anc ane arr axs", AddressingMode.Immediate);

        // The unstable stores, which mix the high byte of their own address into what they write.
        Add(table, "sha", AddressingMode.AbsoluteY, AddressingMode.DirectIndirectY);
        Add(table, "shx tas las", AddressingMode.AbsoluteY);
        Add(table, "shy", AddressingMode.AbsoluteX);

        // Every opcode that stops the processor is one word here, as ca65 spells it.
        Add(table, "jam", AddressingMode.Implied);
        Add(table, "nop",
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        return Freeze(table);
    }

    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build65SC02()
    {
        var table = Copy(mos6502);
        Add(table, "adc and cmp eor lda ora sbc sta", AddressingMode.DirectIndirect);
        Add(table, "bit", AddressingMode.Immediate, AddressingMode.DirectX, AddressingMode.AbsoluteX);
        Add(table, "inc dec", AddressingMode.Accumulator);
        Add(table, "jmp", AddressingMode.AbsoluteIndirectX);
        Add(table, "bra", AddressingMode.Relative);
        Add(table, "phx phy plx ply", AddressingMode.Implied);
        Add(table, "stz",
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX);
        Add(table, "trb tsb", AddressingMode.Direct, AddressingMode.Absolute);
        return Freeze(table);
    }

    /// <summary>The Rockwell bit instructions, which the 65SC02 and the 65816 do not have.</summary>
    private static FrozenDictionary<string, FrozenSet<AddressingMode>> BuildRockwell()
    {
        var table = Copy(cmos65SC02);
        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, $"rmb{bit} smb{bit}", AddressingMode.Direct);
            Add(table, $"bbr{bit} bbs{bit}", AddressingMode.DirectRelative);
        }
        return Freeze(table);
    }

    /// <summary>WDC's 65C02 adds <c>wai</c> and <c>stp</c> to Rockwell's.</summary>
    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build65C02()
    {
        // WDC's own 65C02 also has `jsr (abs,x)` at $fc, and ca65 does not take it before the
        // 65816, so neither does nt65: what nt65 writes has to be what ca65 assembles.
        var table = Copy(rockwell65C02);
        Add(table, "stp wai", AddressingMode.Implied);
        return Freeze(table);
    }

    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build65816()
    {
        // The Rockwell bit instructions are the one part of the 65C02 the 65816 left out.
        var table = Copy(wdc65C02
            .Where(pair => !(pair.Key.Length == 4 && pair.Key[..3] is "bbr" or "bbs" or "rmb" or "smb")));
        Add(table, "adc and cmp eor lda ora sbc sta",
            AddressingMode.Long, AddressingMode.LongX, AddressingMode.DirectIndirectLong,
            AddressingMode.DirectIndirectLongY, AddressingMode.StackRelative, AddressingMode.StackRelativeIndirectY);
        Add(table, "jsr", AddressingMode.AbsoluteIndirectX);
        Add(table, "jml", AddressingMode.Long, AddressingMode.AbsoluteIndirectLong);
        Add(table, "jsl", AddressingMode.Long);
        Add(table, "brl per", AddressingMode.RelativeLong);
        Add(table, "mvn mvp", AddressingMode.BlockMove);
        Add(table, "pea", AddressingMode.Absolute);
        Add(table, "pei", AddressingMode.DirectIndirect);

        // `cop` takes a signature byte as `brk` does, and `wdm` the byte an emulator hooks on.
        Add(table, "rep sep cop wdm", AddressingMode.Immediate);
        Add(table, "phb phd phk plb pld rtl tcd tcs tdc tsc txy tyx xba xce", AddressingMode.Implied);
        return Freeze(table);
    }

    private static Dictionary<string, HashSet<AddressingMode>> Copy(
        IEnumerable<KeyValuePair<string, FrozenSet<AddressingMode>>> table) =>
        table.ToDictionary(pair => pair.Key, pair => new HashSet<AddressingMode>(pair.Value), StringComparer.Ordinal);

    private static void Add(Dictionary<string, HashSet<AddressingMode>> table, string mnemonics,
        params ReadOnlySpan<AddressingMode> modes)
    {
        foreach (var mnemonic in mnemonics.Split(' '))
        {
            if (!table.TryGetValue(mnemonic, out var set))
                table[mnemonic] = set = [];
            foreach (var mode in modes)
                set.Add(mode);
        }
    }

    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Freeze(
        Dictionary<string, HashSet<AddressingMode>> table) =>
        table.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet(), StringComparer.Ordinal);
}
