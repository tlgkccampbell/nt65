using System.Collections.Frozen;
using Norristown.Syntax;
using static Norristown.Syntax.MnemonicKind;

namespace Norristown.Processor;

/// <summary>
/// Records which addressing modes each mnemonic has on each CPU, and how many bytes each mode
/// takes. Syntax does not depend on the CPU, so every form parses on every CPU, and this table
/// decides whether the target actually has it.
/// </summary>
public static class Instructions
{
    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> mos6502 = Build6502();

    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> mos6502X = Build6502X();

    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> cmos65SC02 = Build65SC02();

    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> rockwell65C02 = BuildRockwell();

    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> wdc65C02 = Build65C02();

    private static readonly FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> wdc65816 = Build65816();

    private static readonly FrozenDictionary<MnemonicKind, InstructionFacts> facts = BuildFacts();

    /// <summary>
    /// Returns the facts about <paramref name="mnemonic"/> beyond which modes it has, including
    /// how it affects control flow, what it pushes or pulls, and which registers it leaves
    /// changed.
    /// </summary>
    public static InstructionFacts Facts(MnemonicKind mnemonic) =>
        facts.GetValueOrDefault(mnemonic, InstructionFacts.None);

    /// <summary>
    /// Returns a value indicating whether <paramref name="cpu"/> has <paramref name="mnemonic"/>
    /// at all.
    /// </summary>
    public static bool Has(Cpu cpu, MnemonicKind mnemonic) => Modes(cpu, mnemonic).Count > 0;

    /// <summary>
    /// Returns a value indicating whether a program built for <paramref name="cpu"/> may use
    /// <paramref name="mnemonic"/>. A program may use the CPU's own instructions and the long
    /// branches, which nt65 emits on every CPU.
    /// </summary>
    public static bool Available(Cpu cpu, MnemonicKind mnemonic) =>
        Has(cpu, mnemonic) || SyntaxFacts.IsLongBranch(mnemonic);

    /// <summary>
    /// Returns the modes <paramref name="mnemonic"/> has on <paramref name="cpu"/>, or an empty
    /// set if it has none.
    /// </summary>
    public static IReadOnlySet<AddressingMode> Modes(Cpu cpu, MnemonicKind mnemonic)
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
        return table.GetValueOrDefault(mnemonic, FrozenSet<AddressingMode>.Empty);
    }

    /// <summary>
    /// Returns how many bytes an instruction in <paramref name="mode"/> takes, counting the
    /// opcode and its operand. An immediate counts as one byte here. On the 65816, the immediate
    /// of an instruction for which <see cref="SizedBy"/> names a register is as wide as that
    /// register.
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
    /// Returns the register whose width sizes <paramref name="mnemonic"/>'s immediate on the
    /// 65816, or null if its immediate is always one byte.
    /// </summary>
    public static WidthRegister? SizedBy(MnemonicKind mnemonic) => Facts(mnemonic).SizedBy;

    /// <summary>
    /// Returns how wide the address in an operand of <paramref name="mode"/> is, or null if the
    /// mode has no address to size. The width is one byte for the direct page, two for absolute
    /// and three for long.
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

    /// <summary>
    /// Returns the <c>z:</c>, <c>a:</c> or <c>f:</c> prefix that makes the width of
    /// <paramref name="mode"/> explicit, or null if the mode has no address to size.
    /// </summary>
    public static string? Prefix(AddressingMode mode) => Width(mode) switch
    {
        AddressSize.ZeroPage => "z:",
        AddressSize.Absolute => "a:",
        AddressSize.Far => "f:",
        _ => null,
    };

    /// <summary>
    /// Returns a value indicating whether <paramref name="mnemonic"/>'s operand names a place to
    /// reach rather than an address to size, so that it takes a near or a far target. This is
    /// true of every jump, call and branch, and of <c>per</c>, which reaches its target the way
    /// <c>brl</c> does and pushes it.
    /// </summary>
    public static bool IsControlTransfer(MnemonicKind mnemonic) =>
        Facts(mnemonic).Control is Control.Branches or Control.Jumps or Control.Calls || mnemonic == Per;

    /// <summary>
    /// Returns a value indicating whether <paramref name="mnemonic"/> is a call instruction,
    /// <c>jsr</c> or <c>jsl</c>.
    /// </summary>
    public static bool IsCall(MnemonicKind mnemonic) => Facts(mnemonic).Control == Control.Calls;

    /// <summary>
    /// Returns the two short branches a long branch is emitted with. <c>Taken</c> is the branch
    /// used when the target is in reach, and <c>Skipped</c> is its opposite, which skips over the
    /// <c>jmp</c> when the target is out of reach.
    /// </summary>
    public static (MnemonicKind Taken, MnemonicKind Skipped) FormsOf(MnemonicKind mnemonic) => mnemonic switch
    {
        Jeq => (Beq, Bne),
        Jne => (Bne, Beq),
        Jcs => (Bcs, Bcc),
        Jcc => (Bcc, Bcs),
        Jmi => (Bmi, Bpl),
        Jpl => (Bpl, Bmi),
        Jvs => (Bvs, Bvc),
        Jvc => (Bvc, Bvs),
        _ => throw new ArgumentOutOfRangeException(nameof(mnemonic), mnemonic, "not a long branch"),
    };

    /// <summary>
    /// Returns the long branch that replaces <paramref name="mnemonic"/> where its target is out
    /// of reach, or null if <paramref name="mnemonic"/> is not one of the eight conditional
    /// branches.
    /// </summary>
    public static MnemonicKind? LongFormOf(MnemonicKind mnemonic) => mnemonic switch
    {
        Beq => Jeq,
        Bne => Jne,
        Bcs => Jcs,
        Bcc => Jcc,
        Bmi => Jmi,
        Bpl => Jpl,
        Bvs => Jvs,
        Bvc => Jvc,
        _ => null,
    };

    /// <summary>
    /// Builds a set of facts describing each mnemonic's behavior, shaped to enable the analyses
    /// performed by later passes. Includes which registers each mnemonic writes, which transfer
    /// one register to another, how each affects control flow, which store to memory, what the
    /// stack instructions move, and which immediates the 65816 sizes by a register's width.
    /// </summary>
    private static FrozenDictionary<MnemonicKind, InstructionFacts> BuildFacts()
    {
        var table = new Dictionary<MnemonicKind, InstructionFacts>();

        // A shift or an increment through the accumulator writes it, and one through memory
        // does not. Which flags a `rep` or a `sep` names depends on its operand. These entries
        // are the widest set each can write, and the mode and the operand narrow it.
        Fact(table, [Lda, Pla, Txa, Tya, Tdc, Tsc, Xba, And, Ora, Eor], f => f with { Writes = Registers.A });
        Fact(table, [Adc, Sbc, Asl, Lsr, Rol, Ror], f => f with { Writes = Registers.A | Registers.C });
        Fact(table, [Inc, Dec], f => f with { Writes = Registers.A });
        Fact(table, [Ldx, Plx, Tax, Tsx, Tyx, Inx, Dex], f => f with { Writes = Registers.X });
        Fact(table, [Ldy, Ply, Tay, Txy, Iny, Dey], f => f with { Writes = Registers.Y });
        Fact(table, [Cmp, Cpx, Cpy, Clc, Sec, Plp, Rti, Rep, Sep], f => f with { Writes = Registers.C });

        // A block move counts down in A and walks X and Y along the two banks. Swapping the
        // carry with the emulation flag truncates the index registers and hides half the
        // accumulator, and a software interrupt runs a handler this program may not even contain.
        Fact(table, [Mvn, Mvp], f => f with { Writes = Registers.A | Registers.X | Registers.Y });
        Fact(table, [Xce, Brk, Cop], f => f with { Writes = Registers.All });

        Fact(table, [Tax], f => f with { Copies = (Registers.A, Registers.X) });
        Fact(table, [Tay], f => f with { Copies = (Registers.A, Registers.Y) });
        Fact(table, [Txa], f => f with { Copies = (Registers.X, Registers.A) });
        Fact(table, [Tya], f => f with { Copies = (Registers.Y, Registers.A) });
        Fact(table, [Txy], f => f with { Copies = (Registers.X, Registers.Y) });
        Fact(table, [Tyx], f => f with { Copies = (Registers.Y, Registers.X) });

        // A software interrupt is not among these, because `brk` and `cop` return to the
        // instruction after them, no matter what the handler did to the registers.
        Fact(table, [Bcc, Bcs, Beq, Bmi, Bne, Bpl, Bvc, Bvs], f => f with { Control = Control.Branches });
        Fact(table, [Jeq, Jne, Jcs, Jcc, Jmi, Jpl, Jvs, Jvc], f => f with { Control = Control.Branches });
        for (var bit = 0; bit < 8; bit++)
            Fact(table, [Bbr0 + bit, Bbs0 + bit], f => f with { Control = Control.Branches });
        Fact(table, [Jmp, Jml, Bra, Brl], f => f with { Control = Control.Jumps });
        Fact(table, [Jsr, Jsl], f => f with { Control = Control.Calls });
        Fact(table, [Rts, Rtl, Rti], f => f with { Control = Control.Returns });
        Fact(table, [Stp, Jam], f => f with { Control = Control.Stops });
        Fact(table, [Sta, Stx, Sty, Stz, Inc, Dec, Asl, Lsr, Rol, Ror, Tsb, Trb], f => f with { Stores = true });

        Fact(table, [Pha], f => f with { Pushes = PushSize.Accumulator, Held = Registers.A });
        Fact(table, [Phx], f => f with { Pushes = PushSize.Index, Held = Registers.X });
        Fact(table, [Phy], f => f with { Pushes = PushSize.Index, Held = Registers.Y });
        Fact(table, [Php], f => f with { Pushes = PushSize.OneByte, Held = Registers.C });
        Fact(table, [Phb, Phk], f => f with { Pushes = PushSize.OneByte });
        Fact(table, [Phd, Pea, Pei, Per], f => f with { Pushes = PushSize.TwoBytes });
        Fact(table, [Pla], f => f with { Pulls = PushSize.Accumulator, Held = Registers.A });
        Fact(table, [Plx], f => f with { Pulls = PushSize.Index, Held = Registers.X });
        Fact(table, [Ply], f => f with { Pulls = PushSize.Index, Held = Registers.Y });
        Fact(table, [Plp], f => f with { Pulls = PushSize.OneByte, Held = Registers.C });
        Fact(table, [Plb], f => f with { Pulls = PushSize.OneByte });
        Fact(table, [Pld], f => f with { Pulls = PushSize.TwoBytes });

        Fact(table, [Lda, Adc, And, Bit, Cmp, Eor, Ora, Sbc], f => f with { SizedBy = WidthRegister.A });
        Fact(table, [Ldx, Ldy, Cpx, Cpy], f => f with { SizedBy = WidthRegister.Index });

        // The undocumented opcodes of the NMOS 6502, which only the 6502x has. Each is two of
        // the documented instructions happening at once, so what it writes is what both write.
        Fact(table, [Slo, Rla, Sre, Alr, Anc, Arr], f => f with { Writes = Registers.A | Registers.C });
        Fact(table, [Rra, Isc], f => f with { Writes = Registers.A | Registers.C });
        Fact(table, [Dcp], f => f with { Writes = Registers.C });
        Fact(table, [Axs], f => f with { Writes = Registers.X | Registers.C });
        Fact(table, [Lax, Las], f => f with { Writes = Registers.A | Registers.X });
        Fact(table, [Ane], f => f with { Writes = Registers.A });
        Fact(table, [Slo, Rla, Sre, Rra, Dcp, Isc, Sax, Sha, Shx, Shy, Tas], f => f with { Stores = true });

        // Nothing runs after `jam`, so what it leaves in the registers never matters; it is
        // simply marked as writing all of them.
        Fact(table, [Jam], f => f with { Writes = Registers.All });
        return table.ToFrozenDictionary();
    }

    private static void Fact(
        Dictionary<MnemonicKind, InstructionFacts> table, ReadOnlySpan<MnemonicKind> mnemonics,
        Func<InstructionFacts, InstructionFacts> with)
    {
        foreach (var mnemonic in mnemonics)
            table[mnemonic] = with(table.GetValueOrDefault(mnemonic, InstructionFacts.None));
    }

    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Build6502()
    {
        var table = new Dictionary<MnemonicKind, HashSet<AddressingMode>>();
        Add(table, [Adc, And, Cmp, Eor, Lda, Ora, Sbc],
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX, AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX,
            AddressingMode.DirectIndirectY);
        Add(table, [Sta],
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, [Asl, Lsr, Rol, Ror],
            AddressingMode.Accumulator, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        Add(table, [Inc, Dec],
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX);
        Add(table, [Ldx],
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute,
            AddressingMode.AbsoluteY);
        Add(table, [Ldy],
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        Add(table, [Stx], AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute);
        Add(table, [Sty], AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute);
        Add(table, [Cpx, Cpy], AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.Absolute);
        Add(table, [Bit], AddressingMode.Direct, AddressingMode.Absolute);
        Add(table, [Jmp], AddressingMode.Absolute, AddressingMode.AbsoluteIndirect);
        Add(table, [Jsr], AddressingMode.Absolute);
        Add(table, [Bcc, Bcs, Beq, Bmi, Bne, Bpl, Bvc, Bvs], AddressingMode.Relative);

        // `brk` takes a signature byte on every CPU, and is two bytes wide.
        Add(table, [Brk], AddressingMode.Immediate);
        Add(table,
            [Clc, Cld, Cli, Clv, Dex, Dey, Inx, Iny, Nop, Pha, Php, Pla, Plp, Rti, Rts, Sec, Sed, Sei, Tax, Tay, Tsx, Txa, Txs, Tya],
            AddressingMode.Implied);
        return Freeze(table);
    }

    /// <summary>
    /// Builds the table for the 6502X, which adds the NMOS 6502's undocumented opcodes with
    /// ca65's names and forms, because the output has to assemble under ca65. They are not a
    /// documented instruction set. No datasheet lists them, and which of them a given part runs
    /// the same way is a fact about its silicon. nt65 takes the names, the modes and the
    /// encodings from ca65. It takes what each opcode does, and what it costs, from how NMOS
    /// parts behave, and leaves out anything on which parts disagree.
    /// <para>
    /// One documented instruction also gains modes. <c>nop</c> takes the operands its
    /// undocumented encodings read, so <c>nop $12</c> and <c>nop abs,x</c> are valid on this CPU
    /// and on no other.
    /// </para>
    /// </summary>
    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Build6502X()
    {
        var table = Copy(mos6502);

        // The read-modify-write pairs, each an official instruction folded into another. They
        // take every mode that the store they are built on takes.
        Add(table, [Slo, Rla, Sre, Rra, Dcp, Isc],
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, [Lax],
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute,
            AddressingMode.AbsoluteY, AddressingMode.DirectIndirectX, AddressingMode.DirectIndirectY);
        Add(table, [Sax],
            AddressingMode.Direct, AddressingMode.DirectY, AddressingMode.Absolute, AddressingMode.DirectIndirectX);

        // The opcodes that take only an immediate, which pass A through an operation and the
        // carry or the flags.
        Add(table, [Alr, Anc, Ane, Arr, Axs], AddressingMode.Immediate);

        // The unstable stores, which mix the high byte of their own address into what they write.
        Add(table, [Sha], AddressingMode.AbsoluteY, AddressingMode.DirectIndirectY);
        Add(table, [Shx, Tas, Las], AddressingMode.AbsoluteY);
        Add(table, [Shy], AddressingMode.AbsoluteX);

        // The several opcodes that stop the processor share one mnemonic, `jam`, which is ca65's
        // name for them.
        Add(table, [Jam], AddressingMode.Implied);
        Add(table, [Nop],
            AddressingMode.Immediate, AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute,
            AddressingMode.AbsoluteX);
        return Freeze(table);
    }

    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Build65SC02()
    {
        var table = Copy(mos6502);
        Add(table, [Adc, And, Cmp, Eor, Lda, Ora, Sbc, Sta], AddressingMode.DirectIndirect);
        Add(table, [Bit], AddressingMode.Immediate, AddressingMode.DirectX, AddressingMode.AbsoluteX);
        Add(table, [Inc, Dec], AddressingMode.Accumulator);
        Add(table, [Jmp], AddressingMode.AbsoluteIndirectX);
        Add(table, [Bra], AddressingMode.Relative);
        Add(table, [Phx, Phy, Plx, Ply], AddressingMode.Implied);
        Add(table, [Stz],
            AddressingMode.Direct, AddressingMode.DirectX, AddressingMode.Absolute, AddressingMode.AbsoluteX);
        Add(table, [Trb, Tsb], AddressingMode.Direct, AddressingMode.Absolute);
        return Freeze(table);
    }

    /// <summary>
    /// Builds the table for the Rockwell 65C02, which adds the bit instructions that the 65SC02
    /// and the 65816 do not have.
    /// </summary>
    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> BuildRockwell()
    {
        var table = Copy(cmos65SC02);
        for (var bit = 0; bit < 8; bit++)
        {
            Add(table, [Rmb0 + bit, Smb0 + bit], AddressingMode.Direct);
            Add(table, [Bbr0 + bit, Bbs0 + bit], AddressingMode.DirectRelative);
        }
        return Freeze(table);
    }

    /// <summary>Builds the table for WDC's 65C02, which adds <c>wai</c> and <c>stp</c> to Rockwell's.</summary>
    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Build65C02()
    {
        // WDC's own 65C02 also has `jsr (abs,x)` at $fc, but ca65 does not accept it before the
        // 65816, so neither does nt65, because what nt65 writes has to be what ca65 assembles.
        var table = Copy(rockwell65C02);
        Add(table, [Stp, Wai], AddressingMode.Implied);
        return Freeze(table);
    }

    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Build65816()
    {
        // The Rockwell bit instructions are the one part of the 65C02 the 65816 left out.
        var table = Copy(wdc65C02
            .Where(pair => SyntaxFacts.BitOf(pair.Key) is null));
        Add(table, [Adc, And, Cmp, Eor, Lda, Ora, Sbc, Sta],
            AddressingMode.Long, AddressingMode.LongX, AddressingMode.DirectIndirectLong,
            AddressingMode.DirectIndirectLongY, AddressingMode.StackRelative, AddressingMode.StackRelativeIndirectY);
        Add(table, [Jsr], AddressingMode.AbsoluteIndirectX);
        Add(table, [Jml], AddressingMode.Long, AddressingMode.AbsoluteIndirectLong);
        Add(table, [Jsl], AddressingMode.Long);
        Add(table, [Brl, Per], AddressingMode.RelativeLong);
        Add(table, [Mvn, Mvp], AddressingMode.BlockMove);
        Add(table, [Pea], AddressingMode.Absolute);
        Add(table, [Pei], AddressingMode.DirectIndirect);

        // `cop` takes a signature byte as `brk` does, and `wdm` takes the byte an emulator hooks on.
        Add(table, [Rep, Sep, Cop, Wdm], AddressingMode.Immediate);
        Add(table, [Phb, Phd, Phk, Plb, Pld, Rtl, Tcd, Tcs, Tdc, Tsc, Txy, Tyx, Xba, Xce], AddressingMode.Implied);
        return Freeze(table);
    }

    private static Dictionary<MnemonicKind, HashSet<AddressingMode>> Copy(
        IEnumerable<KeyValuePair<MnemonicKind, FrozenSet<AddressingMode>>> table) =>
        table.ToDictionary(pair => pair.Key, pair => new HashSet<AddressingMode>(pair.Value));

    private static void Add(Dictionary<MnemonicKind, HashSet<AddressingMode>> table, ReadOnlySpan<MnemonicKind> mnemonics,
        params ReadOnlySpan<AddressingMode> modes)
    {
        foreach (var mnemonic in mnemonics)
        {
            if (!table.TryGetValue(mnemonic, out var set))
                table[mnemonic] = set = [];
            foreach (var mode in modes)
                set.Add(mode);
        }
    }

    private static FrozenDictionary<MnemonicKind, FrozenSet<AddressingMode>> Freeze(
        Dictionary<MnemonicKind, HashSet<AddressingMode>> table) =>
        table.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet());
}
