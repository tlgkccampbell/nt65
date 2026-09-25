namespace Norristown.Flow;

/// <summary>
/// Specifies how control leaves a <see cref="BasicBlock"/> after its last statement. Every
/// analysis that asks whether a block runs on, calls, hands control to another routine, returns
/// or stops reads the answer here.
/// </summary>
public enum BlockEnd
{
    /// <summary>
    /// The last statement transfers nothing, so control runs on into the block after it. An empty
    /// block ends this way too.
    /// </summary>
    Through,

    /// <summary>
    /// A conditional branch, which goes to its target when taken and runs on when it is not.
    /// </summary>
    Branch,

    /// <summary>
    /// A call that comes back, made directly, through a pointer or as a relative call. Control
    /// runs on into the block after it once the call returns.
    /// </summary>
    Call,

    /// <summary>A call to a routine that never returns, which is where the path ends.</summary>
    CallNeverReturns,

    /// <summary>
    /// A jump to a label that starts one of the routine's own blocks. The routine's own entry is
    /// such a label, and an analysis that stops at a routine's entry treats a jump there as a tail
    /// call to itself.
    /// </summary>
    Jump,

    /// <summary>
    /// A jump to a place outside the routine, which hands control to another routine for good. That
    /// routine returns to this routine's caller, so the jump is a tail call.
    /// </summary>
    TailCall,

    /// <summary>
    /// A jump to a place the operand does not name, such as an indirect or computed jump, with no
    /// <c>.next</c> to say where it goes.
    /// </summary>
    Elsewhere,

    /// <summary>
    /// A statement other than a call with a <c>.next</c> under it. Control goes only where the
    /// <c>.next</c> says, in place of what the operand says.
    /// </summary>
    Declared,

    /// <summary>A <c>.fallthrough</c>, which runs on into the routine it names.</summary>
    Fallthrough,

    /// <summary>A return, as <c>rts</c>, <c>rtl</c> and <c>rti</c> make, which goes back to the caller.</summary>
    Return,

    /// <summary>
    /// An instruction that stops the processor, as <c>stp</c> and <c>jam</c> do, so that nothing
    /// after it runs until a reset.
    /// </summary>
    Stop,
}
