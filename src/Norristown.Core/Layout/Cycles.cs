using System.Collections.Frozen;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Layout;

/// <summary>
/// How long each instruction takes on each CPU. The count is an interval, because some of
/// what it depends on is not in the program: whether an indexed read crosses a page,
/// whether a branch is taken and crosses one, and on the 65C02 whether the decimal flag
/// is set.
/// <para>
/// On the 65816 most counts depend on the widths and the mode, so they are worked out from
/// the state the analysis found reaching the instruction; where it does not know a width,
/// the interval covers both.
/// </para>
/// </summary>
public static class Cycles
{
    /// <summary>The instructions that only read, so an indexed one pays for a page crossing only sometimes.</summary>
    private const string Reads = "adc and bit cmp cpx cpy eor lda ldx ldy ora sbc";

    /// <summary>The instructions that write, so an indexed one always pays.</summary>
    private const string Writes = "sta stx sty stz";

    /// <summary>The instructions that read, change and write back, which always pay.</summary>
    private const string Modifies = "asl dec inc lsr rol ror";

    private static readonly FrozenDictionary<(string Mnemonic, AddressingMode Mode), CycleCount> mos6502 = Build6502();

    private static readonly FrozenDictionary<(string Mnemonic, AddressingMode Mode), CycleCount> wdc65C02 = Build65C02();

    /// <summary>
    /// How long <paramref name="mnemonic"/> takes in <paramref name="mode"/>, or null when
    /// nt65 has no count for it. A branch is counted as taken or not, both, so its interval
    /// covers the whole of what it can cost.
    /// </summary>
    public static CycleCount? Of(Cpu cpu, string mnemonic, AddressingMode mode, ProcessorState? state = null)
    {
        if (cpu == Cpu.Wdc65816)
            return Of65816(mnemonic.ToLowerInvariant(), mode, state ?? ProcessorState.Unknown);
        var table = cpu == Cpu.Mos6502 ? mos6502 : wdc65C02;
        return table.TryGetValue((mnemonic.ToLowerInvariant(), mode), out var cycles) ? cycles : null;
    }

    /// <summary>
    /// What a long branch costs in the form it was given. Short, it is the branch. Long, the
    /// condition that would have branched now falls into a <c>jmp</c>, and the one that
    /// would not takes the opposite branch over it.
    /// </summary>
    public static CycleCount OfLongBranch(bool inverted) => inverted
        ? new CycleCount(3, 5)
        : new CycleCount(2, 4);

    /// <summary>
    /// A 65816 instruction. The table counts the 8-bit form; a 16-bit register adds a cycle
    /// for each extra byte read or written, two for a read-modify-write, and a 16-bit index
    /// always pays the page-crossing cycle an 8-bit one pays only sometimes. A direct operand
    /// costs one more when the low byte of D is not zero, which is known where D is.
    /// </summary>
    private static CycleCount? Of65816(string mnemonic, AddressingMode mode, ProcessorState state)
    {
        var a = state.A;
        var index = state.Index;
        var direct = Instructions.Width(mode) != AddressSize.ZeroPage ? new CycleCount(0)
            : !state.D.IsKnown ? new CycleCount(0, 1)
            : (state.D.Value & 0xff) != 0 ? new CycleCount(1)
            : new CycleCount(0);

        if (Reads.Split(' ').Contains(mnemonic) || Writes.Split(' ').Contains(mnemonic)
            || mnemonic is "tsb" or "trb" || Modifies.Split(' ').Contains(mnemonic))
        {
            var sized = mnemonic is "ldx" or "ldy" or "cpx" or "cpy" or "stx" or "sty" ? index : a;
            var reads = Reads.Split(' ').Contains(mnemonic);
            var modifies = mnemonic is "tsb" or "trb" || Modifies.Split(' ').Contains(mnemonic);
            if (modifies && mode == AddressingMode.Accumulator)
                return new CycleCount(2);
            int? cost = mode switch
            {
                AddressingMode.Immediate => 2,
                AddressingMode.Direct => modifies ? 5 : 3,
                AddressingMode.DirectX or AddressingMode.DirectY => modifies ? 6 : 4,
                AddressingMode.Absolute => modifies ? 6 : 4,
                AddressingMode.AbsoluteX => modifies ? 7 : reads ? 4 : 5,
                AddressingMode.AbsoluteY => reads ? 4 : 5,
                AddressingMode.Long or AddressingMode.LongX => 5,
                AddressingMode.DirectIndirect => 5,
                AddressingMode.DirectIndirectX => 6,
                AddressingMode.DirectIndirectY => reads ? 5 : 6,
                AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY => 6,
                AddressingMode.StackRelative => 4,
                AddressingMode.StackRelativeIndirectY => 7,
                _ => null,
            };
            if (cost is not { } least)
                return null;
            var total = new CycleCount(least) + Wider(sized, modifies ? 2 : 1) + direct;

            // A read indexed across a page pays for the carry, which a 16-bit index always does.
            if (reads && mode is AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.DirectIndirectY)
                total += index == Width.Sixteen ? new CycleCount(1) : new CycleCount(0, 1);
            return total;
        }

        // A branch costs 2 not taken and 3 taken; only in emulation mode does a taken branch
        // that crosses a page cost one more.
        var crossing = state.E == ProcessorMode.Native ? new CycleCount(0) : new CycleCount(0, 1);
        return (mnemonic, mode) switch
        {
            (_, AddressingMode.Relative) when mnemonic == "bra" => new CycleCount(3) + crossing,
            (_, AddressingMode.Relative) => new CycleCount(2, 3) + crossing,
            ("brl", _) => new CycleCount(4),
            ("per", _) => new CycleCount(6),
            ("pea", _) => new CycleCount(5),
            ("pei", _) => new CycleCount(6) + direct,
            ("jmp", AddressingMode.Absolute) => new CycleCount(3),
            ("jmp", AddressingMode.AbsoluteIndirect) => new CycleCount(5),
            ("jmp", AddressingMode.AbsoluteIndirectX) => new CycleCount(6),
            ("jml", AddressingMode.Long) => new CycleCount(4),
            ("jml", AddressingMode.AbsoluteIndirectLong) => new CycleCount(6),
            ("jsr", AddressingMode.Absolute) => new CycleCount(6),
            ("jsr", AddressingMode.AbsoluteIndirectX) => new CycleCount(8),
            ("jsl", _) => new CycleCount(8),
            ("rts" or "rtl", _) => new CycleCount(6),

            // The native forms push and pull the program bank as well.
            ("rti", _) => new CycleCount(6) + Native(state.E),
            ("brk" or "cop", _) => new CycleCount(7) + Native(state.E),
            ("pha", _) => new CycleCount(3) + Wider(a, 1),
            ("phx" or "phy", _) => new CycleCount(3) + Wider(index, 1),
            ("pla", _) => new CycleCount(4) + Wider(a, 1),
            ("plx" or "ply", _) => new CycleCount(4) + Wider(index, 1),
            ("php" or "phb" or "phk", _) => new CycleCount(3),
            ("phd", _) => new CycleCount(4),
            ("plp" or "plb", _) => new CycleCount(4),
            ("pld", _) => new CycleCount(5),
            ("rep" or "sep" or "stp" or "wai" or "xba", _) => new CycleCount(3),
            ("wdm", _) => new CycleCount(2),

            // A block move takes seven cycles for every byte it moves, and how many that is
            // is in A when it runs.
            ("mvn" or "mvp", _) => null,
            (_, AddressingMode.Implied) => new CycleCount(2),
            _ => null,
        };
    }

    /// <summary>What a register being 16 bits adds: <paramref name="cycles"/> if it is, up to that if unknown.</summary>
    private static CycleCount Wider(Width width, int cycles) => width switch
    {
        Width.Eight => new CycleCount(0),
        Width.Sixteen => new CycleCount(cycles),
        _ => new CycleCount(0, cycles),
    };

    /// <summary>What native mode adds to an interrupt or a return from one.</summary>
    private static CycleCount Native(ProcessorMode mode) => mode switch
    {
        ProcessorMode.Native => new CycleCount(1),
        ProcessorMode.Emulation => new CycleCount(0),
        _ => new CycleCount(0, 1),
    };

    private static Dictionary<(string, AddressingMode), CycleCount> Build()
    {
        var table = new Dictionary<(string, AddressingMode), CycleCount>();

        // The addressing modes that cost the same whatever instruction uses them.
        Add(table, Reads + " " + Writes, AddressingMode.Direct, 3);
        Add(table, Reads + " " + Writes, AddressingMode.DirectX, 4);
        Add(table, Reads + " " + Writes, AddressingMode.DirectY, 4);
        Add(table, Reads + " " + Writes, AddressingMode.Absolute, 4);
        Add(table, Reads, AddressingMode.Immediate, 2);
        Add(table, Reads + " " + Writes, AddressingMode.DirectIndirectX, 6);

        // An indexed read pays one more only when it crosses a page; a write always does,
        // because it cannot begin until the address is settled.
        Add(table, Reads, AddressingMode.AbsoluteX, new CycleCount(4, 5));
        Add(table, Reads, AddressingMode.AbsoluteY, new CycleCount(4, 5));
        Add(table, Reads, AddressingMode.DirectIndirectY, new CycleCount(5, 6));
        Add(table, Writes, AddressingMode.AbsoluteX, 5);
        Add(table, Writes, AddressingMode.AbsoluteY, 5);
        Add(table, Writes, AddressingMode.DirectIndirectY, 6);

        Add(table, Modifies, AddressingMode.Accumulator, 2);
        Add(table, Modifies, AddressingMode.Direct, 5);
        Add(table, Modifies, AddressingMode.DirectX, 6);
        Add(table, Modifies, AddressingMode.Absolute, 6);
        Add(table, Modifies, AddressingMode.AbsoluteX, 7);

        Add(table, "clc cld cli clv dex dey inx iny nop sec sed sei tax tay tsx txa txs tya",
            AddressingMode.Implied, 2);
        Add(table, "pha php", AddressingMode.Implied, 3);
        Add(table, "pla plp", AddressingMode.Implied, 4);
        Add(table, "rts rti", AddressingMode.Implied, 6);
        Add(table, "brk", AddressingMode.Immediate, 7);
        Add(table, "jmp", AddressingMode.Absolute, 3);
        Add(table, "jsr", AddressingMode.Absolute, 6);

        // A branch costs 2 not taken and 3 taken, and one more when a taken branch crosses a
        // page. Which it does is a run-time question, so the interval covers all three.
        Add(table, "bcc bcs beq bmi bne bpl bvc bvs", AddressingMode.Relative, new CycleCount(2, 4));
        return table;
    }

    private static FrozenDictionary<(string, AddressingMode), CycleCount> Build6502()
    {
        var table = Build();

        // The 6502's indirect jump reads its pointer without carrying into the high byte,
        // which is the bug the 65C02 fixes by spending a cycle.
        Add(table, "jmp", AddressingMode.AbsoluteIndirect, 5);
        return table.ToFrozenDictionary();
    }

    private static FrozenDictionary<(string, AddressingMode), CycleCount> Build65C02()
    {
        var table = Build();
        Add(table, "jmp", AddressingMode.AbsoluteIndirect, 6);
        Add(table, "jmp", AddressingMode.AbsoluteIndirectX, 6);
        Add(table, Reads + " " + Writes, AddressingMode.DirectIndirect, 5);
        Add(table, "bit", AddressingMode.Immediate, 2);
        Add(table, "bra", AddressingMode.Relative, new CycleCount(3, 4));
        Add(table, "phx phy", AddressingMode.Implied, 3);
        Add(table, "plx ply", AddressingMode.Implied, 4);
        Add(table, "stp wai", AddressingMode.Implied, 3);
        Add(table, "trb tsb", AddressingMode.Direct, 5);
        Add(table, "trb tsb", AddressingMode.Absolute, 6);

        // A shift indexed absolutely pays for a page crossing only when it crosses one;
        // increment and decrement pay whatever they do.
        Add(table, "asl lsr rol ror", AddressingMode.AbsoluteX, new CycleCount(6, 7));
        Add(table, "inc dec", AddressingMode.AbsoluteX, 7);
        Add(table, "inc dec", AddressingMode.Accumulator, 2);

        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, $"rmb{bit} smb{bit}", AddressingMode.Direct, 5);
            Add(table, $"bbr{bit} bbs{bit}", AddressingMode.DirectRelative, new CycleCount(5, 7));
        }

        // Decimal arithmetic costs one more on the 65C02, and nothing in the program says
        // whether the decimal flag is set where the instruction runs.
        foreach (var mode in table.Keys.Where(key => key.Item1 is "adc" or "sbc").ToList())
            table[mode] = table[mode].Maybe(1);
        return table.ToFrozenDictionary();
    }

    private static void Add(
        Dictionary<(string, AddressingMode), CycleCount> table, string mnemonics, AddressingMode mode, int cycles) =>
        Add(table, mnemonics, mode, new CycleCount(cycles));

    private static void Add(
        Dictionary<(string, AddressingMode), CycleCount> table, string mnemonics, AddressingMode mode,
        CycleCount cycles)
    {
        foreach (var mnemonic in mnemonics.Split(' '))
            table[(mnemonic, mode)] = cycles;
    }
}
