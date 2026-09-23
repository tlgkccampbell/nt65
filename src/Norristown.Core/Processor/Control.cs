namespace Norristown.Processor;

/// <summary>
/// What a mnemonic does to the path running through it, whatever its operand. The addressing
/// mode can still narrow this: a <c>jmp (t)</c> jumps somewhere its operand does not name.
/// </summary>
public enum Control
{
    /// <summary>Carries on to the next instruction, as everything not named below does.</summary>
    Through,

    /// <summary>Goes to its target when a condition holds and carries on when it does not.</summary>
    Branches,

    /// <summary>Always goes to its target and never carries on: <c>jmp</c>, <c>jml</c>, <c>bra</c>, <c>brl</c>.</summary>
    Jumps,

    /// <summary>Calls a subroutine and carries on where it returns to: <c>jsr</c> and <c>jsl</c>.</summary>
    Calls,

    /// <summary>Returns from one: <c>rts</c>, <c>rtl</c> and <c>rti</c>.</summary>
    Returns,

    /// <summary>
    /// Stops the processor, and nothing after it runs until a reset: <c>stp</c> and <c>jam</c>.
    /// A software interrupt is not one of these: the handler's <c>rti</c> comes back to the
    /// instruction after it.
    /// </summary>
    Stops,
}
