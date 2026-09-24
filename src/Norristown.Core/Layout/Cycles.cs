using System.Collections.Frozen;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;
using static Norristown.Syntax.MnemonicKind;

namespace Norristown.Layout;

/// <summary>
/// Provides the cycle count of each instruction on each CPU. The count is an interval, because
/// some of what it depends on is not in the program. That includes whether an indexed read
/// crosses a page, whether a branch is taken and crosses one, and, on the 65C02, whether the
/// decimal flag is set. Every count that is an interval carries the causes of the extra cycles
/// at its top, so a reader never has to guess what decides where in the interval their line
/// falls.
/// <para>
/// On the 65816 most counts depend on the register widths and the processor mode, so they are
/// worked out from the state the analysis found reaching the instruction. Where the analysis
/// does not know a width, the interval covers both widths.
/// </para>
/// </summary>
public static class Cycles
{
    /// <summary>
    /// The cause shown for an indexed or indirect-indexed operand whose address carries into the
    /// high byte.
    /// </summary>
    private const string Crossing = "+1 when the read crosses a page";

    /// <summary>The cause shown for a branch whose condition holds.</summary>
    private const string Taken = "+1 when taken";

    /// <summary>
    /// The cause shown for a taken conditional branch whose target is on another page, where
    /// whether the branch is taken at all is also unknown.
    /// </summary>
    private const string TakenCrossing = "+1 when that crosses a page";

    /// <summary>
    /// The cause shown for a branch that is always taken and whose target is on another page,
    /// where only the page crossing is unknown.
    /// </summary>
    private const string Crosses = "+1 when it crosses a page";

    /// <summary>The cause shown for a direct-page operand on a 65816 whose D the analysis could not follow.</summary>
    private const string DirectPage = "+1 when the low byte of D is not zero";

    /// <summary>
    /// The cause shown for arithmetic on a 65C02, where the program does not say whether the
    /// decimal flag is set.
    /// </summary>
    private const string Decimal = "+1 in decimal mode";

    /// <summary>
    /// The cause shown for an interrupt or its return, where the analysis could not tell whether
    /// the 65816 is in native or emulation mode.
    /// </summary>
    private const string NativeMode = "+1 in native mode";

    /// <summary>
    /// The instructions that only read, so an indexed form takes the extra page-crossing cycle
    /// only when the address actually crosses a page.
    /// </summary>
    private static readonly MnemonicKind[] Reads = [Adc, And, Bit, Cmp, Cpx, Cpy, Eor, Lda, Ldx, Ldy, Ora, Sbc];

    /// <summary>The instructions that write, so an indexed form always takes the page-crossing cycle.</summary>
    private static readonly MnemonicKind[] Writes = [Sta, Stx, Sty, Stz];

    /// <summary>The instructions that read, change and write back, which always take the page-crossing cycle.</summary>
    private static readonly MnemonicKind[] Modifies = [Asl, Dec, Inc, Lsr, Rol, Ror];

    /// <summary>The undocumented opcodes that read, change and write back, which always pay the index cycle.</summary>
    private static readonly MnemonicKind[] Combines = [Slo, Rla, Sre, Rra, Dcp, Isc];

    private static readonly FrozenDictionary<(MnemonicKind Mnemonic, AddressingMode Mode), Timing> mos6502 = Build6502();

    private static readonly FrozenDictionary<(MnemonicKind Mnemonic, AddressingMode Mode), Timing> mos6502X = Build6502X();

    private static readonly FrozenDictionary<(MnemonicKind Mnemonic, AddressingMode Mode), Timing> wdc65C02 = Build65C02();

    /// <summary>
    /// Returns how long <paramref name="mnemonic"/> takes in <paramref name="mode"/>, or null when
    /// nt65 has no count for it. A branch is counted both taken and not taken, so its
    /// interval covers everything it can cost.
    /// </summary>
    public static Timing? Of(Cpu cpu, MnemonicKind mnemonic, AddressingMode mode, ProcessorState? state = null)
    {
        if (cpu == Cpu.Wdc65816)
            return Of65816(mnemonic, mode, state ?? ProcessorState.Unknown);
        var table = cpu switch
        {
            Cpu.Mos6502 => mos6502,
            Cpu.Mos6502X => mos6502X,
            Cpu.Cmos65SC02 or Cpu.Rockwell65C02 or Cpu.Wdc65C02 => wdc65C02,
            _ => throw new ArgumentOutOfRangeException(nameof(cpu), cpu, "not a CPU nt65 knows"),
        };
        return table.TryGetValue((mnemonic, mode), out var cycles) ? cycles : null;
    }

    /// <summary>
    /// Returns what a long branch costs in the form it was laid out in. The short form costs what the
    /// branch costs. In the long form the branch is inverted to skip over a <c>jmp</c>: when
    /// the original condition holds, execution falls through into the <c>jmp</c>, and when it
    /// does not, the inverted branch is taken over it.
    /// </summary>
    public static CycleCount OfLongBranch(bool inverted) => inverted
        ? new CycleCount(3, 5)
        : new CycleCount(2, 4);

    /// <summary>
    /// Returns the timing of a 65816 instruction. The table counts the 8-bit form. A 16-bit
    /// register adds a cycle for each extra byte read or written, or two for a read-modify-write,
    /// and a 16-bit index always pays the page-crossing cycle that an 8-bit index pays only
    /// sometimes. A direct operand costs one more when the low byte of D is not zero, which is
    /// known wherever the analysis knows D.
    /// </summary>
    private static Timing? Of65816(MnemonicKind mnemonic, AddressingMode mode, ProcessorState state)
    {
        var a = state.A;
        var index = state.Index;
        var direct = Instructions.Width(mode) != AddressSize.ZeroPage ? new Timing(0)
            : !state.D.IsKnown ? new Timing(new CycleCount(0, 1), DirectPage)
            : (state.D.Value & 0xff) != 0 ? new Timing(1)
            : new Timing(0);

        if (Reads.Contains(mnemonic) || Writes.Contains(mnemonic)
            || mnemonic is Tsb or Trb || Modifies.Contains(mnemonic))
        {
            var indexed = mnemonic is Ldx or Ldy or Cpx or Cpy or Stx or Sty;
            var sized = indexed ? index : a;
            var reads = Reads.Contains(mnemonic);
            var modifies = mnemonic is Tsb or Trb || Modifies.Contains(mnemonic);
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

            // The terms are added in the order the processor pays them. The direct-page cycle
            // comes while the address is formed, the page crossing while it is indexed, and the
            // second byte of a 16-bit register last of all.
            var total = new Timing(least) + direct;

            // An indexed read takes an extra cycle when it crosses a page, and always takes it
            // with a 16-bit index.
            if (reads && mode is AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.DirectIndirectY)
                total += index == Width.Sixteen ? new Timing(1) : new Timing(new CycleCount(0, 1), Crossing);
            return total + Wider(sized, modifies ? 2 : 1, indexed ? StateRegister.Index : StateRegister.A);
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
            (_, AddressingMode.Relative) when mnemonic == Bra => new Timing(3) + always,
            (_, AddressingMode.Relative) => new Timing(new CycleCount(2, 3), Taken) + crossing,
            (Brl, _) => new Timing(4),
            (Per, _) => new Timing(6),
            (Pea, _) => new Timing(5),
            (Pei, _) => new Timing(6) + direct,
            (Jmp, AddressingMode.Absolute) => new Timing(3),
            (Jmp, AddressingMode.AbsoluteIndirect) => new Timing(5),
            (Jmp, AddressingMode.AbsoluteIndirectX) => new Timing(6),
            (Jml, AddressingMode.Long) => new Timing(4),
            (Jml, AddressingMode.AbsoluteIndirectLong) => new Timing(6),
            (Jsr, AddressingMode.Absolute) => new Timing(6),
            (Jsr, AddressingMode.AbsoluteIndirectX) => new Timing(8),
            (Jsl, _) => new Timing(8),
            (Rts or Rtl, _) => new Timing(6),

            // The native forms push and pull the program bank as well.
            (Rti, _) => new Timing(6) + Native(state.E),
            (Brk or Cop, _) => new Timing(7) + Native(state.E),
            (Pha, _) => new Timing(3) + Wider(a, 1, StateRegister.A),
            (Phx or Phy, _) => new Timing(3) + Wider(index, 1, StateRegister.Index),
            (Pla, _) => new Timing(4) + Wider(a, 1, StateRegister.A),
            (Plx or Ply, _) => new Timing(4) + Wider(index, 1, StateRegister.Index),
            (Php or Phb or Phk, _) => new Timing(3),
            (Phd, _) => new Timing(4),
            (Plp or Plb, _) => new Timing(4),
            (Pld, _) => new Timing(5),
            (Rep or Sep or Stp or Wai or Xba, _) => new Timing(3),
            (Wdm, _) => new Timing(2),

            // A block move takes seven cycles for every byte it moves, and the number of bytes
            // is in A when it runs.
            (Mvn or Mvp, _) => null,
            (_, AddressingMode.Implied) => new Timing(2),
            _ => null,
        };
    }

    /// <summary>
    /// Returns what <paramref name="register"/> being 16 bits adds, given its
    /// <paramref name="width"/>. That is <paramref name="cycles"/> if it is 16 bits, none if it
    /// is 8, and anywhere from none up to <paramref name="cycles"/> where the analysis does not
    /// know the width.
    /// </summary>
    private static Timing Wider(Width width, int cycles, StateRegister register) => width switch
    {
        Width.Eight => new Timing(0),
        Width.Sixteen => new Timing(cycles),
        _ => new Timing(new CycleCount(0, cycles), $"+{cycles} when {register.Name} {register.Is} 16-bit"),
    };

    /// <summary>Returns what native mode adds to an interrupt or a return from one.</summary>
    private static Timing Native(ProcessorMode mode) => mode switch
    {
        ProcessorMode.Native => new Timing(1),
        ProcessorMode.Emulation => new Timing(0),
        _ => new Timing(new CycleCount(0, 1), NativeMode),
    };

    private static Dictionary<(MnemonicKind, AddressingMode), Timing> Build()
    {
        var table = new Dictionary<(MnemonicKind, AddressingMode), Timing>();

        // These addressing modes cost the same for every instruction that uses them.
        Add(table, [.. Reads, .. Writes], AddressingMode.Direct, 3);
        Add(table, [.. Reads, .. Writes], AddressingMode.DirectX, 4);
        Add(table, [.. Reads, .. Writes], AddressingMode.DirectY, 4);
        Add(table, [.. Reads, .. Writes], AddressingMode.Absolute, 4);
        Add(table, Reads, AddressingMode.Immediate, 2);
        Add(table, [.. Reads, .. Writes], AddressingMode.DirectIndirectX, 6);

        // An indexed read pays one more only when it crosses a page. A write always does,
        // because it cannot begin until the address is final.
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

        Add(table, [Clc, Cld, Cli, Clv, Dex, Dey, Inx, Iny, Nop, Sec, Sed, Sei, Tax, Tay, Tsx, Txa, Txs, Tya],
            AddressingMode.Implied, 2);
        Add(table, [Pha, Php], AddressingMode.Implied, 3);
        Add(table, [Pla, Plp], AddressingMode.Implied, 4);
        Add(table, [Rts, Rti], AddressingMode.Implied, 6);
        Add(table, [Brk], AddressingMode.Immediate, 7);
        Add(table, [Jmp], AddressingMode.Absolute, 3);
        Add(table, [Jsr], AddressingMode.Absolute, 6);

        // A branch costs 2 not taken and 3 taken, and one more when a taken branch crosses a
        // page. Which it does is a run-time question, so the interval covers all three.
        Add(table, [Bcc, Bcs, Beq, Bmi, Bne, Bpl, Bvc, Bvs], AddressingMode.Relative,
            new Timing(new CycleCount(2, 4), [Taken, TakenCrossing]));
        return table;
    }

    private static FrozenDictionary<(MnemonicKind, AddressingMode), Timing> Build6502() => Nmos6502().ToFrozenDictionary();

    private static Dictionary<(MnemonicKind, AddressingMode), Timing> Nmos6502()
    {
        var table = Build();

        // The 6502's indirect jump reads its pointer without carrying into the high byte,
        // which is the bug the 65C02 fixes by spending a cycle.
        Add(table, [Jmp], AddressingMode.AbsoluteIndirect, 5);
        return table;
    }

    /// <summary>
    /// Builds the 6502's counts with the undocumented opcodes' counts added. Each undocumented
    /// opcode is two documented instructions in one, and costs what the pair costs. A
    /// read-modify-write always pays the index cycle, and a read pays it only when it crosses a
    /// page.
    /// <para>
    /// What these opcodes leave behind is another matter. The results of <c>ane</c>,
    /// <c>lax #</c> and the stores that mix in the high byte of their own address depend on the
    /// part and on what the bus was last driven with. How long each takes does not, so each is
    /// counted, and hovering over the instruction shows what is unstable about it. <c>jam</c> is
    /// the one opcode with no count, because it stops the processor and there is no next cycle
    /// to reach.
    /// </para>
    /// </summary>
    private static FrozenDictionary<(MnemonicKind, AddressingMode), Timing> Build6502X()
    {
        var table = Nmos6502();
        Add(table, Combines, AddressingMode.Direct, 5);
        Add(table, Combines, AddressingMode.DirectX, 6);
        Add(table, Combines, AddressingMode.Absolute, 6);
        Add(table, Combines, AddressingMode.AbsoluteX, 7);
        Add(table, Combines, AddressingMode.AbsoluteY, 7);
        Add(table, Combines, AddressingMode.DirectIndirectX, 8);
        Add(table, Combines, AddressingMode.DirectIndirectY, 8);

        Add(table, [Lax], AddressingMode.Immediate, 2);
        Add(table, [Lax, Sax], AddressingMode.Direct, 3);
        Add(table, [Lax, Sax], AddressingMode.DirectY, 4);
        Add(table, [Lax, Sax], AddressingMode.Absolute, 4);
        Add(table, [Lax, Sax], AddressingMode.DirectIndirectX, 6);
        Add(table, [Lax, Las], AddressingMode.AbsoluteY, new Timing(new CycleCount(4, 5), Crossing));
        Add(table, [Lax], AddressingMode.DirectIndirectY, new Timing(new CycleCount(5, 6), Crossing));

        Add(table, [Alr, Anc, Ane, Arr, Axs], AddressingMode.Immediate, 2);

        // The stores that mix the high byte of their own address into what they write finish
        // forming the address before they write, as every indexed store does, so each is exact.
        Add(table, [Sha, Shx, Tas], AddressingMode.AbsoluteY, 5);
        Add(table, [Shy], AddressingMode.AbsoluteX, 5);
        Add(table, [Sha], AddressingMode.DirectIndirectY, 6);

        Add(table, [Nop], AddressingMode.Immediate, 2);
        Add(table, [Nop], AddressingMode.Direct, 3);
        Add(table, [Nop], AddressingMode.DirectX, 4);
        Add(table, [Nop], AddressingMode.Absolute, 4);
        Add(table, [Nop], AddressingMode.AbsoluteX, new Timing(new CycleCount(4, 5), Crossing));
        return table.ToFrozenDictionary();
    }

    private static FrozenDictionary<(MnemonicKind, AddressingMode), Timing> Build65C02()
    {
        var table = Build();
        Add(table, [Jmp], AddressingMode.AbsoluteIndirect, 6);
        Add(table, [Jmp], AddressingMode.AbsoluteIndirectX, 6);
        Add(table, [.. Reads, .. Writes], AddressingMode.DirectIndirect, 5);
        Add(table, [Bit], AddressingMode.Immediate, 2);
        Add(table, [Bra], AddressingMode.Relative, new Timing(new CycleCount(3, 4), Crosses));
        Add(table, [Phx, Phy], AddressingMode.Implied, 3);
        Add(table, [Plx, Ply], AddressingMode.Implied, 4);
        Add(table, [Stp, Wai], AddressingMode.Implied, 3);
        Add(table, [Trb, Tsb], AddressingMode.Direct, 5);
        Add(table, [Trb, Tsb], AddressingMode.Absolute, 6);

        // On the 65C02 an absolute-indexed shift takes the page-crossing cycle only when it
        // crosses a page; increment and decrement always take it.
        Add(table, [Asl, Lsr, Rol, Ror], AddressingMode.AbsoluteX, new Timing(new CycleCount(6, 7), Crossing));
        Add(table, [Inc, Dec], AddressingMode.AbsoluteX, 7);
        Add(table, [Inc, Dec], AddressingMode.Accumulator, 2);

        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, [Rmb0 + bit, Smb0 + bit], AddressingMode.Direct, 5);
            Add(table, [Bbr0 + bit, Bbs0 + bit], AddressingMode.DirectRelative,
                new Timing(new CycleCount(5, 7), [Taken, TakenCrossing]));
        }

        // Decimal arithmetic costs one more on the 65C02, and nothing in the program says
        // whether the decimal flag is set where the instruction runs.
        foreach (var mode in table.Keys.Where(key => key.Item1 is Adc or Sbc).ToList())
            table[mode] = table[mode].Maybe(1, Decimal);
        return table.ToFrozenDictionary();
    }

    private static void Add(
        Dictionary<(MnemonicKind, AddressingMode), Timing> table, ReadOnlySpan<MnemonicKind> mnemonics,
        AddressingMode mode, int cycles) =>
        Add(table, mnemonics, mode, new Timing(cycles));

    private static void Add(
        Dictionary<(MnemonicKind, AddressingMode), Timing> table, ReadOnlySpan<MnemonicKind> mnemonics,
        AddressingMode mode, Timing cycles)
    {
        foreach (var mnemonic in mnemonics)
            table[(mnemonic, mode)] = cycles;
    }
}
