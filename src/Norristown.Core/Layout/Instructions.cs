using System.Collections.Frozen;
using Norristown.Project;

namespace Norristown.Layout;

/// <summary>
/// Which addressing modes each mnemonic has, on each CPU, and how long each one is. Syntax does not depend on the CPU, so every form parses everywhere and this table
/// is what says whether the target actually has it.
/// <para>
/// The 65816 arrives with Stage 11; until then this table covers the 6502 and the 65C02.
/// </para>
/// </summary>
public static class Instructions
{
    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> mos6502 = Build6502();

    private static readonly FrozenDictionary<string, FrozenSet<AddressingMode>> wdc65C02 = Build65C02();

    /// <summary>Whether <paramref name="cpu"/> has <paramref name="mnemonic"/> at all.</summary>
    public static bool Has(Cpu cpu, string mnemonic) => Modes(cpu, mnemonic).Count > 0;

    /// <summary>The modes <paramref name="mnemonic"/> has on <paramref name="cpu"/>, empty if it has none.</summary>
    public static IReadOnlySet<AddressingMode> Modes(Cpu cpu, string mnemonic)
    {
        var table = cpu == Cpu.Mos6502 ? mos6502 : wdc65C02;
        return table.GetValueOrDefault(mnemonic.ToLowerInvariant(), FrozenSet<AddressingMode>.Empty);
    }

    /// <summary>
    /// How many bytes an instruction in <paramref name="mode"/> takes: the opcode and its
    /// operand. On the 6502 and the 65C02 an immediate is always one byte; the 65816's
    /// width-dependent immediates arrive with Stage 11.
    /// </summary>
    public static int Length(AddressingMode mode) => mode switch
    {
        AddressingMode.Implied or AddressingMode.Accumulator => 1,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX
            or AddressingMode.DirectRelative => 3,
        _ => 2,
    };

    /// <summary>
    /// How wide the address in an operand of this mode is, or null where the mode carries no
    /// address to size: one byte for the direct page, two for absolute.
    /// </summary>
    public static Semantics.AddressSize? Width(AddressingMode mode) => mode switch
    {
        AddressingMode.Direct or AddressingMode.DirectX or AddressingMode.DirectY
            or AddressingMode.DirectIndirect or AddressingMode.DirectIndirectX
            or AddressingMode.DirectIndirectY or AddressingMode.DirectRelative => Semantics.AddressSize.ZeroPage,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX => Semantics.AddressSize.Absolute,
        _ => null,
    };

    /// <summary>The <c>z:</c>, <c>a:</c> or <c>f:</c> that makes a mode's width explicit.</summary>
    public static string? Prefix(AddressingMode mode) => Width(mode) switch
    {
        Semantics.AddressSize.ZeroPage => "z:",
        Semantics.AddressSize.Absolute => "a:",
        _ => null,
    };

    /// <summary>Whether a mnemonic transfers control, so its target is near or far rather than sized.</summary>
    public static bool IsControlTransfer(string mnemonic) =>
        Modes(Cpu.Wdc65C02, mnemonic).Any(mode => mode is AddressingMode.Relative or AddressingMode.DirectRelative)
        || Syntax.SyntaxFacts.LongBranches.Contains(mnemonic)
        || mnemonic.Equals("jmp", StringComparison.OrdinalIgnoreCase)
        || mnemonic.Equals("jsr", StringComparison.OrdinalIgnoreCase);

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

    private static FrozenDictionary<string, FrozenSet<AddressingMode>> Build65C02()
    {
        var table = mos6502.ToDictionary(pair => pair.Key, pair => new HashSet<AddressingMode>(pair.Value),
            StringComparer.Ordinal);
        Add(table, "adc and cmp eor lda ora sbc sta", AddressingMode.DirectIndirect);
        Add(table, "bit", AddressingMode.Immediate, AddressingMode.DirectX, AddressingMode.AbsoluteX);
        Add(table, "inc dec", AddressingMode.Accumulator);
        Add(table, "jmp", AddressingMode.AbsoluteIndirectX);
        Add(table, "bra", AddressingMode.Relative);
        Add(table, "phx phy plx ply stp wai", AddressingMode.Implied);
        Add(table, "stz",
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX);
        Add(table, "trb tsb", AddressingMode.Direct, AddressingMode.Absolute);

        // The Rockwell bit instructions, which the 65816 does not have.
        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, $"rmb{bit} smb{bit}", AddressingMode.Direct);
            Add(table, $"bbr{bit} bbs{bit}", AddressingMode.DirectRelative);
        }
        return Freeze(table);
    }

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
