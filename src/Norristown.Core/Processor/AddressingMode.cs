namespace Norristown.Processor;

/// <summary>
/// Specifies an addressing mode, in the form ca65 uses for an operand. The direct, absolute
/// and long modes differ only in how wide the address is, but they are separate modes,
/// because choosing among them is all the address-size rule does.
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

    /// <summary>Through a direct-page pointer: <c>lda (ptr)</c>, on the CMOS 6502s and up.</summary>
    DirectIndirect,

    /// <summary>Indexed indirect: <c>lda (ptr,x)</c>.</summary>
    DirectIndirectX,

    /// <summary>Indirect indexed: <c>lda (ptr),y</c>.</summary>
    DirectIndirectY,

    /// <summary>Through an absolute pointer: <c>jmp (vector)</c>.</summary>
    AbsoluteIndirect,

    /// <summary>Through a table of pointers: <c>jmp (vector,x)</c>, on the CMOS 6502s and up.</summary>
    AbsoluteIndirectX,

    /// <summary>A branch target, one signed byte away: <c>bne @loop</c>.</summary>
    Relative,

    /// <summary>A direct-page address and a branch target: <c>bbr0 flags, @skip</c>.</summary>
    DirectRelative,

    /// <summary>Three bytes of address: <c>lda f:table</c>, on the 65816.</summary>
    Long,

    /// <summary>Long, indexed by X: <c>lda f:table,x</c>, on the 65816.</summary>
    LongX,

    /// <summary>Through a three-byte direct-page pointer: <c>lda [ptr]</c>, on the 65816.</summary>
    DirectIndirectLong,

    /// <summary>Through a three-byte direct-page pointer, indexed by Y: <c>lda [ptr],y</c>, on the 65816.</summary>
    DirectIndirectLongY,

    /// <summary>An offset from the stack pointer: <c>lda 3,s</c>, on the 65816.</summary>
    StackRelative,

    /// <summary>Through a pointer on the stack, indexed by Y: <c>lda (3,s),y</c>, on the 65816.</summary>
    StackRelativeIndirectY,

    /// <summary>Through a three-byte absolute pointer: <c>jml [vector]</c>, on the 65816.</summary>
    AbsoluteIndirectLong,

    /// <summary>A target two signed bytes away: <c>brl @far</c> and <c>per @label</c>, on the 65816.</summary>
    RelativeLong,

    /// <summary>A source bank and a destination bank: <c>mvn #src, #dst</c>, on the 65816.</summary>
    BlockMove,
}
