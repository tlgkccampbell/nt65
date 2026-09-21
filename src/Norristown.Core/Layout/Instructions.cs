using System.Collections.Frozen;
using Norristown.Project;

namespace Norristown.Layout;

/// <summary>
/// Which addressing modes each mnemonic has, on each CPU, and how long each one is. Syntax
/// does not depend on the CPU, so every form parses everywhere and this table is what says
/// whether the target actually has it.
/// </summary>
public static class Instructions
{
    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> mos6502 = Build6502();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> cmos65SC02 = Build65SC02();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> rockwell65C02 = BuildRockwell();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> wdc65C02 = Build65C02();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> wdc65816 = Build65816();

    /// <summary>Whether <paramref name="cpu"/> has <paramref name="mnemonic"/> at all.</summary>
    public static bool Has(Cpu cpu, string mnemonic) => Modes(cpu, mnemonic).Count > 0;

    /// <summary>
    /// Whether a program built for <paramref name="cpu"/> may write <paramref name="mnemonic"/>:
    /// the CPU's own instructions, and the long branches, which nt65 writes on every CPU.
    /// </summary>
    public static bool Writable(Cpu cpu, string mnemonic) =>
        Has(cpu, mnemonic) || Syntax.SyntaxFacts.LongBranches.Contains(mnemonic);

    /// <summary>The modes <paramref name="mnemonic"/> has on <paramref name="cpu"/>, empty if it has none.</summary>
    public static IReadOnlySet<AddressingMode> Modes(Cpu cpu, string mnemonic)
    {
        var table = cpu switch
        {
            Cpu.Mos6502 => mos6502,
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
    /// or null when its immediate is always one byte. ca65 sizes exactly these from its
    /// <c>.a8</c>/<c>.a16</c> and <c>.i8</c>/<c>.i16</c> settings.
    /// </summary>
    public static WidthRegister? SizedBy(string mnemonic) => mnemonic.ToLowerInvariant() switch
    {
        "lda" or "adc" or "and" or "bit" or "cmp" or "eor" or "ora" or "sbc" => WidthRegister.A,
        "ldx" or "ldy" or "cpx" or "cpy" => WidthRegister.Index,
        _ => null,
    };

    /// <summary>
    /// How wide the address in an operand of this mode is, or null where the mode carries no
    /// address to size: one byte for the direct page, two for absolute and three for long.
    /// </summary>
    public static Semantics.AddressSize? Width(AddressingMode mode) => mode switch
    {
        AddressingMode.Direct or AddressingMode.DirectX or AddressingMode.DirectY
            or AddressingMode.DirectIndirect or AddressingMode.DirectIndirectX
            or AddressingMode.DirectIndirectY or AddressingMode.DirectRelative
            or AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY => Semantics.AddressSize.ZeroPage,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX
            or AddressingMode.AbsoluteIndirectLong => Semantics.AddressSize.Absolute,
        AddressingMode.Long or AddressingMode.LongX => Semantics.AddressSize.Far,
        _ => null,
    };

    /// <summary>The <c>z:</c>, <c>a:</c> or <c>f:</c> that makes a mode's width explicit.</summary>
    public static string? Prefix(AddressingMode mode) => Width(mode) switch
    {
        Semantics.AddressSize.ZeroPage => "z:",
        Semantics.AddressSize.Absolute => "a:",
        Semantics.AddressSize.Far => "f:",
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
        || Syntax.SyntaxFacts.LongBranches.Contains(mnemonic)
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
