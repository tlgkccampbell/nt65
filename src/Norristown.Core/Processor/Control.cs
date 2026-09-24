namespace Norristown.Processor;

/// <summary>
/// Specifies how a mnemonic affects the control flow through it, independent of its operand.
/// The addressing mode can still narrow this; for example, <c>jmp (t)</c> jumps to a place its
/// operand does not name.
/// </summary>
public enum Control
{
    /// <summary>Continues to the next instruction. Every mnemonic not named below does this.</summary>
    Through,

    /// <summary>Goes to its target when a condition holds, and continues when it does not.</summary>
    Branches,

    /// <summary>
    /// Always goes to its target and never continues, as <c>jmp</c>, <c>jml</c>, <c>bra</c> and
    /// <c>brl</c> do.
    /// </summary>
    Jumps,

    /// <summary>
    /// Calls a subroutine and continues where it returns, as <c>jsr</c> and <c>jsl</c> do.
    /// </summary>
    Calls,

    /// <summary>
    /// Returns from a subroutine or an interrupt, as <c>rts</c>, <c>rtl</c> and <c>rti</c> do.
    /// </summary>
    Returns,

    /// <summary>
    /// Stops the processor, so that nothing after it runs until a reset, as <c>stp</c> and
    /// <c>jam</c> do. A software interrupt does not stop the processor, because the handler's
    /// <c>rti</c> returns to the instruction after it.
    /// </summary>
    Stops,
}
