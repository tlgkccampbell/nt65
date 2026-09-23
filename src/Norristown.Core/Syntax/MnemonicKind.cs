namespace Norristown.Syntax;

/// <summary>
/// Which instruction a mnemonic token names. The lexer reads every mnemonic, whatever its case,
/// as one of these, so the passes after it compare kinds rather than spellings; each member's
/// name, lower case, is how it is written.
/// <para>
/// These are the canonical WDC mnemonics, and the undocumented opcodes of the NMOS 6502 in
/// ca65's spellings, since those have no canonical name of their own. ca65's alternative 65816
/// spellings (<c>tad</c>, <c>swa</c> and the rest) are ordinary identifiers. The exception is
/// <c>tas</c>, which is the NMOS undocumented opcode, not ca65's alternative spelling.
/// </para>
/// </summary>
public enum MnemonicKind
{
    /// <summary>Not a mnemonic: the kind of every token that is not one.</summary>
    None,

    // The NMOS 6502's documented instructions.
    /// <summary><c>adc</c>: add with carry.</summary>
    Adc,

    /// <summary><c>and</c>: and accumulator.</summary>
    And,

    /// <summary><c>asl</c>: arithmetic shift left.</summary>
    Asl,

    /// <summary><c>bcc</c>: branch on carry clear.</summary>
    Bcc,

    /// <summary><c>bcs</c>: branch on carry set.</summary>
    Bcs,

    /// <summary><c>beq</c>: branch on equal.</summary>
    Beq,

    /// <summary><c>bit</c>: test memory bits.</summary>
    Bit,

    /// <summary><c>bmi</c>: branch on minus.</summary>
    Bmi,

    /// <summary><c>bne</c>: branch on not equal.</summary>
    Bne,

    /// <summary><c>bpl</c>: branch on plus.</summary>
    Bpl,

    /// <summary><c>brk</c>: software break.</summary>
    Brk,

    /// <summary><c>bvc</c>: branch on overflow clear.</summary>
    Bvc,

    /// <summary><c>bvs</c>: branch on overflow set.</summary>
    Bvs,

    /// <summary><c>clc</c>: clear carry.</summary>
    Clc,

    /// <summary><c>cld</c>: clear decimal mode.</summary>
    Cld,

    /// <summary><c>cli</c>: clear interrupt disable.</summary>
    Cli,

    /// <summary><c>clv</c>: V.</summary>
    Clv,

    /// <summary><c>cmp</c>: compare accumulator.</summary>
    Cmp,

    /// <summary><c>cpx</c>: compare index x.</summary>
    Cpx,

    /// <summary><c>cpy</c>: N Z C.</summary>
    Cpy,

    /// <summary><c>dec</c>: decrement.</summary>
    Dec,

    /// <summary><c>dex</c>: decrement index x.</summary>
    Dex,

    /// <summary><c>dey</c>: N Z.</summary>
    Dey,

    /// <summary><c>eor</c>: exclusive or accumulator.</summary>
    Eor,

    /// <summary><c>inc</c>: increment.</summary>
    Inc,

    /// <summary><c>inx</c>: increment index x.</summary>
    Inx,

    /// <summary><c>iny</c>: increment index y.</summary>
    Iny,

    /// <summary><c>jmp</c>: jump.</summary>
    Jmp,

    /// <summary><c>jsr</c>: jump to subroutine.</summary>
    Jsr,

    /// <summary><c>lda</c>: load accumulator.</summary>
    Lda,

    /// <summary><c>ldx</c>: load index x.</summary>
    Ldx,

    /// <summary><c>ldy</c>: N Z.</summary>
    Ldy,

    /// <summary><c>lsr</c>: logical shift right.</summary>
    Lsr,

    /// <summary><c>nop</c>: no operation.</summary>
    Nop,

    /// <summary><c>ora</c>: or accumulator.</summary>
    Ora,

    /// <summary><c>pha</c>: push accumulator.</summary>
    Pha,

    /// <summary><c>php</c>: push processor status.</summary>
    Php,

    /// <summary><c>pla</c>: pull accumulator.</summary>
    Pla,

    /// <summary><c>plp</c>: pull processor status.</summary>
    Plp,

    /// <summary><c>rol</c>: rotate left.</summary>
    Rol,

    /// <summary><c>ror</c>: N Z C.</summary>
    Ror,

    /// <summary><c>rti</c>: return from interrupt.</summary>
    Rti,

    /// <summary><c>rts</c>: return from subroutine.</summary>
    Rts,

    /// <summary><c>sbc</c>: N V Z C.</summary>
    Sbc,

    /// <summary><c>sec</c>: set carry.</summary>
    Sec,

    /// <summary><c>sed</c>: D.</summary>
    Sed,

    /// <summary><c>sei</c>: I.</summary>
    Sei,

    /// <summary><c>sta</c>: store accumulator.</summary>
    Sta,

    /// <summary><c>stx</c>: store index x.</summary>
    Stx,

    /// <summary><c>sty</c>: store index y.</summary>
    Sty,

    /// <summary><c>tax</c>: transfer accumulator to x.</summary>
    Tax,

    /// <summary><c>tay</c>: transfer accumulator to y.</summary>
    Tay,

    /// <summary><c>tsx</c>: transfer stack pointer to x.</summary>
    Tsx,

    /// <summary><c>txa</c>: transfer x to accumulator.</summary>
    Txa,

    /// <summary><c>txs</c>: transfer x to stack pointer.</summary>
    Txs,

    /// <summary><c>tya</c>: transfer y to accumulator.</summary>
    Tya,

    // The NMOS 6502's undocumented opcodes, in ca65's spellings, which only the 6502x has.
    /// <summary><c>alr</c>: and accumulator, then shift right.</summary>
    Alr,

    /// <summary><c>anc</c>: and accumulator, then copy the sign into carry.</summary>
    Anc,

    /// <summary><c>ane</c>: N Z.</summary>
    Ane,

    /// <summary><c>arr</c>: N V Z C.</summary>
    Arr,

    /// <summary><c>axs</c>: N Z C.</summary>
    Axs,

    /// <summary><c>dcp</c>: decrement, then compare accumulator.</summary>
    Dcp,

    /// <summary><c>isc</c>: increment, then subtract with borrow.</summary>
    Isc,

    /// <summary><c>jam</c>: stop the processor.</summary>
    Jam,

    /// <summary><c>las</c>: and the stack pointer, into the accumulator, x and it.</summary>
    Las,

    /// <summary><c>lax</c>: load accumulator and index x.</summary>
    Lax,

    /// <summary><c>rla</c>: rotate left, then and accumulator.</summary>
    Rla,

    /// <summary><c>rra</c>: rotate right, then add with carry.</summary>
    Rra,

    /// <summary><c>sax</c>: store accumulator and index x.</summary>
    Sax,

    /// <summary><c>sha</c>: store accumulator, x and the address high byte (unstable).</summary>
    Sha,

    /// <summary><c>shx</c>: store index x and the address high byte (unstable).</summary>
    Shx,

    /// <summary><c>shy</c>: store index y and the address high byte (unstable).</summary>
    Shy,

    /// <summary><c>slo</c>: shift left, then or accumulator.</summary>
    Slo,

    /// <summary><c>sre</c>: shift right, then exclusive or accumulator.</summary>
    Sre,

    /// <summary><c>tas</c>: transfer accumulator and x to the stack pointer, then store (unstable).</summary>
    Tas,

    // What the 65C02s add.
    /// <summary><c>bra</c>: branch always.</summary>
    Bra,

    /// <summary><c>phx</c>: push index x.</summary>
    Phx,

    /// <summary><c>phy</c>: push index y.</summary>
    Phy,

    /// <summary><c>plx</c>: pull index x.</summary>
    Plx,

    /// <summary><c>ply</c>: pull index y.</summary>
    Ply,

    /// <summary><c>stz</c>: store zero.</summary>
    Stz,

    /// <summary><c>trb</c>: test and reset bits.</summary>
    Trb,

    /// <summary><c>tsb</c>: Z.</summary>
    Tsb,

    /// <summary><c>stp</c>: stop the clock.</summary>
    Stp,

    /// <summary><c>wai</c>: wait for interrupt.</summary>
    Wai,

    // The Rockwell bit instructions, each family together and in bit order, which is what
    // BitOf relies on.
    /// <summary><c>bbr0</c>: branch on bit reset 0.</summary>
    Bbr0,

    /// <summary><c>bbr1</c>: branch on bit reset 1.</summary>
    Bbr1,

    /// <summary><c>bbr2</c>: branch on bit reset 2.</summary>
    Bbr2,

    /// <summary><c>bbr3</c>: branch on bit reset 3.</summary>
    Bbr3,

    /// <summary><c>bbr4</c>: branch on bit reset 4.</summary>
    Bbr4,

    /// <summary><c>bbr5</c>: branch on bit reset 5.</summary>
    Bbr5,

    /// <summary><c>bbr6</c>: branch on bit reset 6.</summary>
    Bbr6,

    /// <summary><c>bbr7</c>: branch on bit reset 7.</summary>
    Bbr7,

    /// <summary><c>bbs0</c>: branch on bit set 0.</summary>
    Bbs0,

    /// <summary><c>bbs1</c>: branch on bit set 1.</summary>
    Bbs1,

    /// <summary><c>bbs2</c>: branch on bit set 2.</summary>
    Bbs2,

    /// <summary><c>bbs3</c>: branch on bit set 3.</summary>
    Bbs3,

    /// <summary><c>bbs4</c>: branch on bit set 4.</summary>
    Bbs4,

    /// <summary><c>bbs5</c>: branch on bit set 5.</summary>
    Bbs5,

    /// <summary><c>bbs6</c>: branch on bit set 6.</summary>
    Bbs6,

    /// <summary><c>bbs7</c>: branch on bit set 7.</summary>
    Bbs7,

    /// <summary><c>rmb0</c>: reset memory bit 0.</summary>
    Rmb0,

    /// <summary><c>rmb1</c>: reset memory bit 1.</summary>
    Rmb1,

    /// <summary><c>rmb2</c>: reset memory bit 2.</summary>
    Rmb2,

    /// <summary><c>rmb3</c>: reset memory bit 3.</summary>
    Rmb3,

    /// <summary><c>rmb4</c>: reset memory bit 4.</summary>
    Rmb4,

    /// <summary><c>rmb5</c>: reset memory bit 5.</summary>
    Rmb5,

    /// <summary><c>rmb6</c>: reset memory bit 6.</summary>
    Rmb6,

    /// <summary><c>rmb7</c>: reset memory bit 7.</summary>
    Rmb7,

    /// <summary><c>smb0</c>: set memory bit 0.</summary>
    Smb0,

    /// <summary><c>smb1</c>: set memory bit 1.</summary>
    Smb1,

    /// <summary><c>smb2</c>: set memory bit 2.</summary>
    Smb2,

    /// <summary><c>smb3</c>: set memory bit 3.</summary>
    Smb3,

    /// <summary><c>smb4</c>: set memory bit 4.</summary>
    Smb4,

    /// <summary><c>smb5</c>: set memory bit 5.</summary>
    Smb5,

    /// <summary><c>smb6</c>: set memory bit 6.</summary>
    Smb6,

    /// <summary><c>smb7</c>: set memory bit 7.</summary>
    Smb7,

    // What the 65816 adds.
    /// <summary><c>brl</c>: branch long.</summary>
    Brl,

    /// <summary><c>cop</c>: coprocessor enable.</summary>
    Cop,

    /// <summary><c>jml</c>: jump long.</summary>
    Jml,

    /// <summary><c>jsl</c>: jump to subroutine long.</summary>
    Jsl,

    /// <summary><c>mvn</c>: move block negative.</summary>
    Mvn,

    /// <summary><c>mvp</c>: move block positive.</summary>
    Mvp,

    /// <summary><c>pea</c>: push effective absolute address.</summary>
    Pea,

    /// <summary><c>pei</c>: push effective indirect address.</summary>
    Pei,

    /// <summary><c>per</c>: push effective pc relative address.</summary>
    Per,

    /// <summary><c>phb</c>: push data bank.</summary>
    Phb,

    /// <summary><c>phd</c>: push direct register.</summary>
    Phd,

    /// <summary><c>phk</c>: push program bank.</summary>
    Phk,

    /// <summary><c>plb</c>: pull data bank.</summary>
    Plb,

    /// <summary><c>pld</c>: N Z.</summary>
    Pld,

    /// <summary><c>rep</c>: reset status bits.</summary>
    Rep,

    /// <summary><c>rtl</c>: return from subroutine long.</summary>
    Rtl,

    /// <summary><c>sep</c>: set status bits.</summary>
    Sep,

    /// <summary><c>tcd</c>: transfer c to direct register.</summary>
    Tcd,

    /// <summary><c>tcs</c>: transfer c to stack pointer.</summary>
    Tcs,

    /// <summary><c>tdc</c>: transfer direct register to c.</summary>
    Tdc,

    /// <summary><c>tsc</c>: transfer stack pointer to c.</summary>
    Tsc,

    /// <summary><c>txy</c>: transfer x to y.</summary>
    Txy,

    /// <summary><c>tyx</c>: N Z.</summary>
    Tyx,

    /// <summary><c>wdm</c>: reserved for expansion.</summary>
    Wdm,

    /// <summary><c>xba</c>: N Z.</summary>
    Xba,

    /// <summary><c>xce</c>: C.</summary>
    Xce,

    // The long branches, which nt65 writes on every CPU, kept together and last, which is what
    // IsLongBranch relies on.
    /// <summary><c>jeq</c>: long branch on equal.</summary>
    Jeq,

    /// <summary><c>jne</c>: long branch on not equal.</summary>
    Jne,

    /// <summary><c>jcs</c>: long branch on carry set.</summary>
    Jcs,

    /// <summary><c>jcc</c>: long branch on carry clear.</summary>
    Jcc,

    /// <summary><c>jmi</c>: long branch on minus.</summary>
    Jmi,

    /// <summary><c>jpl</c>: long branch on plus.</summary>
    Jpl,

    /// <summary><c>jvs</c>: long branch on overflow set.</summary>
    Jvs,

    /// <summary><c>jvc</c>: long branch on overflow clear.</summary>
    Jvc,
}
