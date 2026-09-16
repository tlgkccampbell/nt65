namespace Norristown.Layout;

/// <summary>
/// An addressing mode, as ca65 spells it in an operand (§7.1). The modes that differ only
/// by how wide the address is — direct, absolute and long — are separate modes, because
/// choosing between them is what §7.2 is about.
/// </summary>
public enum AddressingMode
{
    /// <summary>No operand: <c>inx</c>.</summary>
    Implied,

    /// <summary>The accumulator: <c>asl a</c>.</summary>
    Accumulator,

    /// <summary>A value: <c>lda #$10</c>.</summary>
    Immediate,

    /// <summary>Direct page, one byte of address: <c>lda z:ptr</c>.</summary>
    Direct,

    /// <summary>Direct, indexed by X: <c>lda z:ptr,x</c>.</summary>
    DirectX,

    /// <summary>Direct, indexed by Y: <c>ldx z:ptr,y</c>.</summary>
    DirectY,

    /// <summary>Two bytes of address: <c>lda a:table</c>.</summary>
    Absolute,

    /// <summary>Absolute, indexed by X: <c>lda a:table,x</c>.</summary>
    AbsoluteX,

    /// <summary>Absolute, indexed by Y: <c>lda a:table,y</c>.</summary>
    AbsoluteY,

    /// <summary>Through a direct-page pointer: <c>lda (ptr)</c>, on the 65C02 and up.</summary>
    DirectIndirect,

    /// <summary>Indexed indirect: <c>lda (ptr,x)</c>.</summary>
    DirectIndirectX,

    /// <summary>Indirect indexed: <c>lda (ptr),y</c>.</summary>
    DirectIndirectY,

    /// <summary>Through an absolute pointer: <c>jmp (vector)</c>.</summary>
    AbsoluteIndirect,

    /// <summary>Through a table of pointers: <c>jmp (vector,x)</c>, on the 65C02 and up.</summary>
    AbsoluteIndirectX,

    /// <summary>A branch target, one signed byte away: <c>bne @loop</c>.</summary>
    Relative,

    /// <summary>A direct-page address and a branch target: <c>bbr0 flags, @skip</c>.</summary>
    DirectRelative,
}
