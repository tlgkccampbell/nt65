using System.Collections.Frozen;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Layout;

/// <summary>
/// How long each instruction takes on each CPU. The count is an interval, because some of
/// what it depends on is not in the program: whether an indexed read crosses a page,
/// whether a branch is taken and crosses one, and on the 65C02 whether the decimal flag
/// is set. Every count that is an interval carries the causes of the extra cycles at its top,
/// so a reader never has to guess what decides where in the interval their line falls.
/// <para>
/// On the 65816 most counts depend on the widths and the mode, so they are worked out from
/// the state the analysis found reaching the instruction; where it does not know a width,
/// the interval covers both.
/// </para>
/// </summary>
public static class Cycles
{
    /// <summary>
    /// The instructions that only read, so an indexed form takes the extra page-crossing cycle
    /// only when the address actually crosses a page.
    /// </summary>
    private const string Reads = "adc and bit cmp cpx cpy eor lda ldx ldy ora sbc";

    /// <summary>The instructions that write, so an indexed form always takes the page-crossing cycle.</summary>
    private const string Writes = "sta stx sty stz";

    /// <summary>The instructions that read, change and write back, which always take the page-crossing cycle.</summary>
    private const string Modifies = "asl dec inc lsr rol ror";

    /// <summary>An indexed or indirect-indexed operand whose address carries into the high byte.</summary>
    private const string Crossing = "+1 when the read crosses a page";

    /// <summary>A branch whose condition holds.</summary>
    private const string Taken = "+1 when taken";

    /// <summary>
    /// A taken branch whose target is on another page, for a conditional branch, where whether
    /// the branch is taken at all is also unknown.
    /// </summary>
    private const string TakenCrossing = "+1 when that crosses a page";

    /// <summary>The same, for a branch that is always taken, where only the page crossing is unknown.</summary>
    private const string Crosses = "+1 when it crosses a page";

    /// <summary>A direct-page operand on a 65816 whose D the analysis could not follow.</summary>
    private const string DirectPage = "+1 when the low byte of D is not zero";

    /// <summary>Arithmetic on a 65C02, where the program does not say whether the decimal flag is set.</summary>
    private const string Decimal = "+1 in decimal mode";

    /// <summary>
    /// An interrupt or its return, where the analysis could not tell whether the 65816 is in
    /// native or emulation mode.
    /// </summary>
    private const string NativeMode = "+1 in native mode";

    /// <summary>The undocumented opcodes that read, change and write back, which always pay the index cycle.</summary>
    private const string Combines = "slo rla sre rra dcp isc";

    private static readonly FrozenDictionary<(string Mnemonic, AddressingMode Mode), Timing> mos6502 = Build6502();

    private static readonly FrozenDictionary<(string Mnemonic, AddressingMode Mode), Timing> mos6502X = Build6502X();

    private static readonly FrozenDictionary<(string Mnemonic, AddressingMode Mode), Timing> wdc65C02 = Build65C02();

    /// <summary>
    /// How long <paramref name="mnemonic"/> takes in <paramref name="mode"/>, or null when
    /// nt65 has no count for it. A branch is counted both taken and not taken, so its
    /// interval covers everything it can cost.
    /// </summary>
    public static Timing? Of(Cpu cpu, string mnemonic, AddressingMode mode, ProcessorState? state = null)
    {
        if (cpu == Cpu.Wdc65816)
            return Of65816(mnemonic.ToLowerInvariant(), mode, state ?? ProcessorState.Unknown);
        var table = cpu switch
        {
            Cpu.Mos6502 => mos6502,
            Cpu.Mos6502X => mos6502X,
            _ => wdc65C02,
        };
        return table.TryGetValue((mnemonic.ToLowerInvariant(), mode), out var cycles) ? cycles : null;
    }

    /// <summary>
    /// What a long branch costs in the form it was laid out in. The short form costs what the
    /// branch costs. In the long form the branch is inverted to skip over a <c>jmp</c>: when
    /// the original condition holds, execution falls through into the <c>jmp</c>, and when it
    /// does not, the inverted branch is taken over it.
    /// </summary>
    public static CycleCount OfLongBranch(bool inverted) => inverted
        ? new CycleCount(3, 5)
        : new CycleCount(2, 4);

    /// <summary>
    /// A 65816 instruction. The table counts the 8-bit form; a 16-bit register adds a cycle
    /// for each extra byte read or written, two for a read-modify-write, and a 16-bit index
    /// always pays the page-crossing cycle an 8-bit one pays only sometimes. A direct operand
    /// costs one more when the low byte of D is not zero, which is known wherever the analysis
    /// knows D.
    /// </summary>
    private static Timing? Of65816(string mnemonic, AddressingMode mode, ProcessorState state)
    {
        var a = state.A;
        var index = state.Index;
        var direct = Instructions.Width(mode) != AddressSize.ZeroPage ? new Timing(0)
            : !state.D.IsKnown ? new Timing(new CycleCount(0, 1), DirectPage)
            : (state.D.Value & 0xff) != 0 ? new Timing(1)
            : new Timing(0);

        if (Reads.Split(' ').Contains(mnemonic) || Writes.Split(' ').Contains(mnemonic)
            || mnemonic is "tsb" or "trb" || Modifies.Split(' ').Contains(mnemonic))
        {
            var indexed = mnemonic is "ldx" or "ldy" or "cpx" or "cpy" or "stx" or "sty";
            var sized = indexed ? index : a;
            var reads = Reads.Split(' ').Contains(mnemonic);
            var modifies = mnemonic is "tsb" or "trb" || Modifies.Split(' ').Contains(mnemonic);
            if (modifies && mode == AddressingMode.Accumulator)
                return new Timing(2);
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

            // The terms are added in the order the processor pays them: the direct page while
            // the address is formed, the page crossing while it is indexed, and the second
            // byte of a 16-bit register last of all.
            var total = new Timing(least) + direct;

            // An indexed read takes an extra cycle when it crosses a page, and always takes it
            // with a 16-bit index.
            if (reads && mode is AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.DirectIndirectY)
                total += index == Width.Sixteen ? new Timing(1) : new Timing(new CycleCount(0, 1), Crossing);
            return total + Wider(sized, modifies ? 2 : 1, indexed ? "X and Y" : "A");
        }

        // A branch costs 2 not taken and 3 taken; only in emulation mode does a taken branch
        // that crosses a page cost one more.
        var crossing = state.E == ProcessorMode.Native
            ? new Timing(0)
            : new Timing(new CycleCount(0, 1), TakenCrossing);
        var always = state.E == ProcessorMode.Native
            ? new Timing(0)
            : new Timing(new CycleCount(0, 1), Crosses);
        return (mnemonic, mode) switch
        {
            (_, AddressingMode.Relative) when mnemonic == "bra" => new Timing(3) + always,
            (_, AddressingMode.Relative) => new Timing(new CycleCount(2, 3), Taken) + crossing,
            ("brl", _) => new Timing(4),
            ("per", _) => new Timing(6),
            ("pea", _) => new Timing(5),
            ("pei", _) => new Timing(6) + direct,
            ("jmp", AddressingMode.Absolute) => new Timing(3),
            ("jmp", AddressingMode.AbsoluteIndirect) => new Timing(5),
            ("jmp", AddressingMode.AbsoluteIndirectX) => new Timing(6),
            ("jml", AddressingMode.Long) => new Timing(4),
            ("jml", AddressingMode.AbsoluteIndirectLong) => new Timing(6),
            ("jsr", AddressingMode.Absolute) => new Timing(6),
            ("jsr", AddressingMode.AbsoluteIndirectX) => new Timing(8),
            ("jsl", _) => new Timing(8),
            ("rts" or "rtl", _) => new Timing(6),

            // The native forms push and pull the program bank as well.
            ("rti", _) => new Timing(6) + Native(state.E),
            ("brk" or "cop", _) => new Timing(7) + Native(state.E),
            ("pha", _) => new Timing(3) + Wider(a, 1, "A"),
            ("phx" or "phy", _) => new Timing(3) + Wider(index, 1, "X and Y"),
            ("pla", _) => new Timing(4) + Wider(a, 1, "A"),
            ("plx" or "ply", _) => new Timing(4) + Wider(index, 1, "X and Y"),
            ("php" or "phb" or "phk", _) => new Timing(3),
            ("phd", _) => new Timing(4),
            ("plp" or "plb", _) => new Timing(4),
            ("pld", _) => new Timing(5),
            ("rep" or "sep" or "stp" or "wai" or "xba", _) => new Timing(3),
            ("wdm", _) => new Timing(2),

            // A block move takes seven cycles for every byte it moves, and how many that is
            // is in A when it runs.
            ("mvn" or "mvp", _) => null,
            (_, AddressingMode.Implied) => new Timing(2),
            _ => null,
        };
    }

    /// <summary>
    /// What <paramref name="register"/> being 16 bits adds, given its <paramref name="width"/>:
    /// <paramref name="cycles"/> if it is 16 bits, none if it is 8, and anywhere from none up
    /// to <paramref name="cycles"/> where the analysis does not know the width.
    /// </summary>
    private static Timing Wider(Width width, int cycles, string register) => width switch
    {
        Width.Eight => new Timing(0),
        Width.Sixteen => new Timing(cycles),
        _ => new Timing(new CycleCount(0, cycles), $"+{cycles} when {register} is 16-bit"),
    };

    /// <summary>What native mode adds to an interrupt or a return from one.</summary>
    private static Timing Native(ProcessorMode mode) => mode switch
    {
        ProcessorMode.Native => new Timing(1),
        ProcessorMode.Emulation => new Timing(0),
        _ => new Timing(new CycleCount(0, 1), NativeMode),
    };

    private static Dictionary<(string, AddressingMode), Timing> Build()
    {
        var table = new Dictionary<(string, AddressingMode), Timing>();

        // The addressing modes that cost the same whatever instruction uses them.
        Add(table, Reads + " " + Writes, AddressingMode.Direct, 3);
        Add(table, Reads + " " + Writes, AddressingMode.DirectX, 4);
        Add(table, Reads + " " + Writes, AddressingMode.DirectY, 4);
        Add(table, Reads + " " + Writes, AddressingMode.Absolute, 4);
        Add(table, Reads, AddressingMode.Immediate, 2);
        Add(table, Reads + " " + Writes, AddressingMode.DirectIndirectX, 6);

        // An indexed read pays one more only when it crosses a page; a write always does,
        // because it cannot begin until the address is settled.
        Add(table, Reads, AddressingMode.AbsoluteX, new Timing(new CycleCount(4, 5), Crossing));
        Add(table, Reads, AddressingMode.AbsoluteY, new Timing(new CycleCount(4, 5), Crossing));
        Add(table, Reads, AddressingMode.DirectIndirectY, new Timing(new CycleCount(5, 6), Crossing));
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
        Add(table, "bcc bcs beq bmi bne bpl bvc bvs", AddressingMode.Relative,
            new Timing(new CycleCount(2, 4), [Taken, TakenCrossing]));
        return table;
    }

    private static FrozenDictionary<(string, AddressingMode), Timing> Build6502() => Nmos6502().ToFrozenDictionary();

    private static Dictionary<(string, AddressingMode), Timing> Nmos6502()
    {
        var table = Build();

        // The 6502's indirect jump reads its pointer without carrying into the high byte,
        // which is the bug the 65C02 fixes by spending a cycle.
        Add(table, "jmp", AddressingMode.AbsoluteIndirect, 5);
        return table;
    }

    /// <summary>
    /// The 6502's counts, and the undocumented opcodes' on top of them. Each of those is two
    /// documented instructions in one, and costs what the pair costs: a read-modify-write pays
    /// the index cycle whatever it does, and a read pays it only when it crosses a page.
    /// <para>
    /// What these opcodes leave behind is another matter — <c>ane</c>, <c>lax #</c> and the
    /// stores that mix in the high byte of their own address depend on the part and on what the
    /// bus was last driven with — but how long each takes does not, so each is counted and the
    /// instruction says on hover what is unstable about it. <c>jam</c> is the one with no count:
    /// it stops the processor, and there is no next cycle to reach.
    /// </para>
    /// </summary>
    private static FrozenDictionary<(string, AddressingMode), Timing> Build6502X()
    {
        var table = Nmos6502();
        Add(table, Combines, AddressingMode.Direct, 5);
        Add(table, Combines, AddressingMode.DirectX, 6);
        Add(table, Combines, AddressingMode.Absolute, 6);
        Add(table, Combines, AddressingMode.AbsoluteX, 7);
        Add(table, Combines, AddressingMode.AbsoluteY, 7);
        Add(table, Combines, AddressingMode.DirectIndirectX, 8);
        Add(table, Combines, AddressingMode.DirectIndirectY, 8);

        Add(table, "lax", AddressingMode.Immediate, 2);
        Add(table, "lax sax", AddressingMode.Direct, 3);
        Add(table, "lax sax", AddressingMode.DirectY, 4);
        Add(table, "lax sax", AddressingMode.Absolute, 4);
        Add(table, "lax sax", AddressingMode.DirectIndirectX, 6);
        Add(table, "lax las", AddressingMode.AbsoluteY, new Timing(new CycleCount(4, 5), Crossing));
        Add(table, "lax", AddressingMode.DirectIndirectY, new Timing(new CycleCount(5, 6), Crossing));

        Add(table, "alr anc ane arr axs", AddressingMode.Immediate, 2);

        // The stores that mix the high byte of their own address into what they write settle
        // the address before they write, as every indexed store does, so each is exact.
        Add(table, "sha shx tas", AddressingMode.AbsoluteY, 5);
        Add(table, "shy", AddressingMode.AbsoluteX, 5);
        Add(table, "sha", AddressingMode.DirectIndirectY, 6);

        Add(table, "nop", AddressingMode.Immediate, 2);
        Add(table, "nop", AddressingMode.Direct, 3);
        Add(table, "nop", AddressingMode.DirectX, 4);
        Add(table, "nop", AddressingMode.Absolute, 4);
        Add(table, "nop", AddressingMode.AbsoluteX, new Timing(new CycleCount(4, 5), Crossing));
        return table.ToFrozenDictionary();
    }

    private static FrozenDictionary<(string, AddressingMode), Timing> Build65C02()
    {
        var table = Build();
        Add(table, "jmp", AddressingMode.AbsoluteIndirect, 6);
        Add(table, "jmp", AddressingMode.AbsoluteIndirectX, 6);
        Add(table, Reads + " " + Writes, AddressingMode.DirectIndirect, 5);
        Add(table, "bit", AddressingMode.Immediate, 2);
        Add(table, "bra", AddressingMode.Relative, new Timing(new CycleCount(3, 4), Crosses));
        Add(table, "phx phy", AddressingMode.Implied, 3);
        Add(table, "plx ply", AddressingMode.Implied, 4);
        Add(table, "stp wai", AddressingMode.Implied, 3);
        Add(table, "trb tsb", AddressingMode.Direct, 5);
        Add(table, "trb tsb", AddressingMode.Absolute, 6);

        // On the 65C02 an absolute-indexed shift takes the page-crossing cycle only when it
        // crosses a page; increment and decrement always take it.
        Add(table, "asl lsr rol ror", AddressingMode.AbsoluteX, new Timing(new CycleCount(6, 7), Crossing));
        Add(table, "inc dec", AddressingMode.AbsoluteX, 7);
        Add(table, "inc dec", AddressingMode.Accumulator, 2);

        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, $"rmb{bit} smb{bit}", AddressingMode.Direct, 5);
            Add(table, $"bbr{bit} bbs{bit}", AddressingMode.DirectRelative,
                new Timing(new CycleCount(5, 7), [Taken, TakenCrossing]));
        }

        // Decimal arithmetic costs one more on the 65C02, and nothing in the program says
        // whether the decimal flag is set where the instruction runs.
        foreach (var mode in table.Keys.Where(key => key.Item1 is "adc" or "sbc").ToList())
            table[mode] = table[mode].Maybe(1, Decimal);
        return table.ToFrozenDictionary();
    }

    private static void Add(
        Dictionary<(string, AddressingMode), Timing> table, string mnemonics, AddressingMode mode, int cycles) =>
        Add(table, mnemonics, mode, new Timing(cycles));

    private static void Add(
        Dictionary<(string, AddressingMode), Timing> table, string mnemonics, AddressingMode mode, Timing cycles)
    {
        foreach (var mnemonic in mnemonics.Split(' '))
            table[(mnemonic, mode)] = cycles;
    }
}
