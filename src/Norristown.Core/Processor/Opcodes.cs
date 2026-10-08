using System.Collections.Frozen;
using Norristown.Syntax;
using static Norristown.Processor.AddressingMode;
using static Norristown.Syntax.MnemonicKind;

namespace Norristown.Processor;

/// <summary>
/// Records the opcode byte of each instruction form on each CPU, both ways: the byte an
/// instruction is written as, and the instruction a byte runs as. Where several bytes run as one
/// form, as with the NMOS 6502's undocumented <c>nop</c> encodings, an instruction is written as
/// the byte ca65 chooses, and every one of them decodes.
/// </summary>
public static class Opcodes
{
    // The groups of eight that share a column pattern. Each mnemonic's opcodes are its base plus
    // the offset of the mode.
    private static readonly MnemonicKind[] alu = [Ora, And, Eor, Adc, Sta, Lda, Cmp, Sbc];

    private static readonly MnemonicKind[] combined = [Slo, Rla, Sre, Rra, Sax, Lax, Dcp, Isc];

    private static readonly FrozenDictionary<Cpu, Table> tables = Build();

    /// <summary>
    /// Returns the opcode byte that <paramref name="mnemonic"/> in <paramref name="mode"/> is
    /// written as on <paramref name="cpu"/>, or null when the CPU has no such form.
    /// </summary>
    public static byte? Encode(Cpu cpu, MnemonicKind mnemonic, AddressingMode mode) =>
        tables[cpu].Encodings.TryGetValue((mnemonic, mode), out var opcode) ? opcode : null;

    /// <summary>
    /// Returns the instruction <paramref name="opcode"/> runs as on <paramref name="cpu"/>, or
    /// null when the CPU has no instruction with that byte.
    /// </summary>
    public static (MnemonicKind Mnemonic, AddressingMode Mode)? Decode(Cpu cpu, byte opcode) =>
        tables[cpu].Decodings[opcode];

    private static FrozenDictionary<Cpu, Table> Build()
    {
        var nmos = new Table();
        Nmos(nmos);
        var undocumented = nmos.Copy();
        Undocumented(undocumented);
        var cmos = nmos.Copy();
        Cmos(cmos);
        var rockwell = cmos.Copy();
        Rockwell(rockwell);
        var wdc = rockwell.Copy();
        wdc.Add(Wai, Implied, 0xCB);
        wdc.Add(Stp, Implied, 0xDB);

        // The 65816 reuses the bytes of the Rockwell bit instructions for its long modes.
        var native = cmos.Copy();
        native.Add(Wai, Implied, 0xCB);
        native.Add(Stp, Implied, 0xDB);
        Native(native);
        return new Dictionary<Cpu, Table>
        {
            [Cpu.Mos6502] = nmos,
            [Cpu.Mos6502X] = undocumented,
            [Cpu.Cmos65SC02] = cmos,
            [Cpu.Rockwell65C02] = rockwell,
            [Cpu.Wdc65C02] = wdc,
            [Cpu.Wdc65816] = native,
        }.ToFrozenDictionary();
    }

    private static void Nmos(Table table)
    {
        for (var i = 0; i < alu.Length; i++)
        {
            var at = i * 0x20;
            table.Add(alu[i], DirectIndirectX, at + 0x01);
            table.Add(alu[i], Direct, at + 0x05);
            if (alu[i] != Sta)
                table.Add(alu[i], Immediate, at + 0x09);
            table.Add(alu[i], Absolute, at + 0x0D);
            table.Add(alu[i], DirectIndirectY, at + 0x11);
            table.Add(alu[i], DirectX, at + 0x15);
            table.Add(alu[i], AbsoluteY, at + 0x19);
            table.Add(alu[i], AbsoluteX, at + 0x1D);
        }
        MnemonicKind[] shifts = [Asl, Rol, Lsr, Ror];
        for (var i = 0; i < shifts.Length; i++)
        {
            var at = i * 0x20;
            table.Add(shifts[i], Accumulator, at + 0x0A);
            Memory(table, shifts[i], at);
        }
        Memory(table, Dec, 0xC0);
        Memory(table, Inc, 0xE0);

        table.Add(Ldx, Immediate, 0xA2);
        table.Add(Ldx, Direct, 0xA6);
        table.Add(Ldx, DirectY, 0xB6);
        table.Add(Ldx, Absolute, 0xAE);
        table.Add(Ldx, AbsoluteY, 0xBE);
        table.Add(Ldy, Immediate, 0xA0);
        table.Add(Ldy, Direct, 0xA4);
        table.Add(Ldy, DirectX, 0xB4);
        table.Add(Ldy, Absolute, 0xAC);
        table.Add(Ldy, AbsoluteX, 0xBC);
        table.Add(Stx, Direct, 0x86);
        table.Add(Stx, DirectY, 0x96);
        table.Add(Stx, Absolute, 0x8E);
        table.Add(Sty, Direct, 0x84);
        table.Add(Sty, DirectX, 0x94);
        table.Add(Sty, Absolute, 0x8C);
        table.Add(Cpx, Immediate, 0xE0);
        table.Add(Cpx, Direct, 0xE4);
        table.Add(Cpx, Absolute, 0xEC);
        table.Add(Cpy, Immediate, 0xC0);
        table.Add(Cpy, Direct, 0xC4);
        table.Add(Cpy, Absolute, 0xCC);
        table.Add(Bit, Direct, 0x24);
        table.Add(Bit, Absolute, 0x2C);
        table.Add(Jmp, Absolute, 0x4C);
        table.Add(Jmp, AbsoluteIndirect, 0x6C);
        table.Add(Jsr, Absolute, 0x20);
        table.Add(Brk, Immediate, 0x00);

        MnemonicKind[] branches = [Bpl, Bmi, Bvc, Bvs, Bcc, Bcs, Bne, Beq];
        for (var i = 0; i < branches.Length; i++)
            table.Add(branches[i], Relative, 0x10 + (i * 0x20));

        (MnemonicKind, int)[] implied =
        [
            (Php, 0x08), (Clc, 0x18), (Plp, 0x28), (Sec, 0x38), (Rti, 0x40), (Pha, 0x48), (Cli, 0x58),
            (Rts, 0x60), (Pla, 0x68), (Sei, 0x78), (Dey, 0x88), (Txa, 0x8A), (Tya, 0x98), (Txs, 0x9A),
            (Tay, 0xA8), (Tax, 0xAA), (Clv, 0xB8), (Tsx, 0xBA), (Iny, 0xC8), (Dex, 0xCA), (Cld, 0xD8),
            (Inx, 0xE8), (Nop, 0xEA), (Sed, 0xF8),
        ];
        foreach (var (mnemonic, opcode) in implied)
            table.Add(mnemonic, Implied, opcode);
    }

    /// <summary>
    /// Adds the NMOS 6502's undocumented opcodes. Where several bytes run as one form, the first
    /// listed is the one ca65 writes.
    /// </summary>
    private static void Undocumented(Table table)
    {
        for (var i = 0; i < combined.Length; i++)
        {
            var at = i * 0x20;
            var mnemonic = combined[i];
            table.Add(mnemonic, DirectIndirectX, at + 0x03);
            table.Add(mnemonic, Direct, at + 0x07);
            table.Add(mnemonic, Absolute, at + 0x0F);
            if (mnemonic == Sax)
            {
                table.Add(Sax, DirectY, 0x97);
                continue;
            }
            table.Add(mnemonic, DirectIndirectY, at + 0x13);
            if (mnemonic == Lax)
            {
                table.Add(Lax, Immediate, 0xAB);
                table.Add(Lax, DirectY, 0xB7);
                table.Add(Lax, AbsoluteY, 0xBF);
                continue;
            }
            table.Add(mnemonic, DirectX, at + 0x17);
            table.Add(mnemonic, AbsoluteY, at + 0x1B);
            table.Add(mnemonic, AbsoluteX, at + 0x1F);
        }
        table.Add(Anc, Immediate, 0x0B);
        table.Add(Anc, Immediate, 0x2B);
        table.Add(Alr, Immediate, 0x4B);
        table.Add(Arr, Immediate, 0x6B);
        table.Add(Ane, Immediate, 0x8B);
        table.Add(Axs, Immediate, 0xCB);
        table.Add(Sbc, Immediate, 0xEB);
        table.Add(Sha, DirectIndirectY, 0x93);
        table.Add(Sha, AbsoluteY, 0x9F);
        table.Add(Tas, AbsoluteY, 0x9B);
        table.Add(Shy, AbsoluteX, 0x9C);
        table.Add(Shx, AbsoluteY, 0x9E);
        table.Add(Las, AbsoluteY, 0xBB);

        foreach (var opcode in (int[])[0x1A, 0x3A, 0x5A, 0x7A, 0xDA, 0xFA])
            table.Add(Nop, Implied, opcode);
        foreach (var opcode in (int[])[0x80, 0x82, 0x89, 0xC2, 0xE2])
            table.Add(Nop, Immediate, opcode);
        foreach (var opcode in (int[])[0x04, 0x44, 0x64])
            table.Add(Nop, Direct, opcode);
        foreach (var opcode in (int[])[0x14, 0x34, 0x54, 0x74, 0xD4, 0xF4])
            table.Add(Nop, DirectX, opcode);
        table.Add(Nop, Absolute, 0x0C);
        foreach (var opcode in (int[])[0x1C, 0x3C, 0x5C, 0x7C, 0xDC, 0xFC])
            table.Add(Nop, AbsoluteX, opcode);
        foreach (var opcode in (int[])[0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72, 0x92, 0xB2, 0xD2, 0xF2])
            table.Add(Jam, Implied, opcode);
    }

    private static void Cmos(Table table)
    {
        for (var i = 0; i < alu.Length; i++)
            table.Add(alu[i], DirectIndirect, (i * 0x20) + 0x12);
        table.Add(Bit, Immediate, 0x89);
        table.Add(Bit, DirectX, 0x34);
        table.Add(Bit, AbsoluteX, 0x3C);
        table.Add(Inc, Accumulator, 0x1A);
        table.Add(Dec, Accumulator, 0x3A);
        table.Add(Jmp, AbsoluteIndirectX, 0x7C);
        table.Add(Bra, Relative, 0x80);
        table.Add(Phy, Implied, 0x5A);
        table.Add(Ply, Implied, 0x7A);
        table.Add(Phx, Implied, 0xDA);
        table.Add(Plx, Implied, 0xFA);
        table.Add(Stz, Direct, 0x64);
        table.Add(Stz, DirectX, 0x74);
        table.Add(Stz, Absolute, 0x9C);
        table.Add(Stz, AbsoluteX, 0x9E);
        table.Add(Tsb, Direct, 0x04);
        table.Add(Tsb, Absolute, 0x0C);
        table.Add(Trb, Direct, 0x14);
        table.Add(Trb, Absolute, 0x1C);
    }

    private static void Rockwell(Table table)
    {
        for (var bit = 0; bit < 8; bit++)
        {
            table.Add(Rmb0 + bit, Direct, 0x07 + (bit * 0x10));
            table.Add(Smb0 + bit, Direct, 0x87 + (bit * 0x10));
            table.Add(Bbr0 + bit, DirectRelative, 0x0F + (bit * 0x10));
            table.Add(Bbs0 + bit, DirectRelative, 0x8F + (bit * 0x10));
        }
    }

    private static void Native(Table table)
    {
        for (var i = 0; i < alu.Length; i++)
        {
            var at = i * 0x20;
            table.Add(alu[i], StackRelative, at + 0x03);
            table.Add(alu[i], DirectIndirectLong, at + 0x07);
            table.Add(alu[i], Long, at + 0x0F);
            table.Add(alu[i], StackRelativeIndirectY, at + 0x13);
            table.Add(alu[i], DirectIndirectLongY, at + 0x17);
            table.Add(alu[i], LongX, at + 0x1F);
        }
        table.Add(Cop, Immediate, 0x02);
        table.Add(Jsl, Long, 0x22);
        table.Add(Wdm, Immediate, 0x42);
        table.Add(Mvp, BlockMove, 0x44);
        table.Add(Mvn, BlockMove, 0x54);
        table.Add(Jml, Long, 0x5C);
        table.Add(Jml, AbsoluteIndirectLong, 0xDC);
        table.Add(Per, RelativeLong, 0x62);
        table.Add(Brl, RelativeLong, 0x82);
        table.Add(Rep, Immediate, 0xC2);
        table.Add(Sep, Immediate, 0xE2);
        table.Add(Pei, DirectIndirect, 0xD4);
        table.Add(Pea, Absolute, 0xF4);
        table.Add(Jsr, AbsoluteIndirectX, 0xFC);
        (MnemonicKind, int)[] implied =
        [
            (Phd, 0x0B), (Tcs, 0x1B), (Pld, 0x2B), (Tsc, 0x3B), (Phk, 0x4B), (Tcd, 0x5B), (Rtl, 0x6B),
            (Tdc, 0x7B), (Phb, 0x8B), (Txy, 0x9B), (Plb, 0xAB), (Tyx, 0xBB), (Xba, 0xEB), (Xce, 0xFB),
        ];
        foreach (var (mnemonic, opcode) in implied)
            table.Add(mnemonic, Implied, opcode);
    }

    /// <summary>Adds the four memory forms a shift, an increment or a decrement has.</summary>
    private static void Memory(Table table, MnemonicKind mnemonic, int at)
    {
        table.Add(mnemonic, Direct, at + 0x06);
        table.Add(mnemonic, Absolute, at + 0x0E);
        table.Add(mnemonic, DirectX, at + 0x16);
        table.Add(mnemonic, AbsoluteX, at + 0x1E);
    }

    /// <summary>Represents the opcodes of one CPU, both ways.</summary>
    private sealed class Table
    {
        /// <summary>Gets the byte each form is written as.</summary>
        public Dictionary<(MnemonicKind, AddressingMode), byte> Encodings { get; } = [];

        /// <summary>Gets the form each byte runs as, or null for a byte the CPU has no instruction for.</summary>
        public (MnemonicKind Mnemonic, AddressingMode Mode)?[] Decodings { get; } = new (MnemonicKind, AddressingMode)?[256];

        /// <summary>
        /// Adds <paramref name="opcode"/> as a byte that runs as <paramref name="mnemonic"/> in
        /// <paramref name="mode"/>. The first byte added for a form is the one it is written as.
        /// </summary>
        public void Add(MnemonicKind mnemonic, AddressingMode mode, int opcode)
        {
            Encodings.TryAdd((mnemonic, mode), (byte)opcode);
            Decodings[opcode] = (mnemonic, mode);
        }

        /// <summary>Returns a table that holds what this one holds, to add another CPU's opcodes to.</summary>
        public Table Copy()
        {
            var copy = new Table();
            foreach (var (form, opcode) in Encodings)
                copy.Encodings[form] = opcode;
            Decodings.CopyTo(copy.Decodings, 0);
            return copy;
        }
    }
}
