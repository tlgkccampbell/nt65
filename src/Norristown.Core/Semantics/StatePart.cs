namespace Norristown.Semantics;

/// <summary>Specifies which part of the processor state a state item is about.</summary>
public enum StatePart
{
    /// <summary>The accumulator's width: <c>a8</c>, <c>a16</c>, <c>a?</c>, <c>a*</c>.</summary>
    A,

    /// <summary>The index registers' width: <c>i8</c>, <c>i16</c>, <c>i?</c>, <c>i*</c>.</summary>
    Index,

    /// <summary>The emulation flag: <c>native</c>, <c>emu</c>, <c>e?</c>, <c>e*</c>.</summary>
    E,

    /// <summary>How a routine is called and left: <c>near</c> or <c>far</c>.</summary>
    Distance,

    /// <summary>Data placed after each call: <c>inline n</c>.</summary>
    Inline,

    /// <summary>The direct page: <c>dp = e</c>, <c>dp?</c>, <c>dp*</c>.</summary>
    DirectPage,

    /// <summary>The data bank: <c>dbr = e</c>, <c>dbr?</c>, <c>dbr*</c>.</summary>
    DataBank,

    /// <summary>What the caller pushes before the call: <c>args n</c>.</summary>
    Arguments,

    /// <summary>
    /// A routine the processor enters on an interrupt, and that leaves by <c>rti</c>:
    /// <c>interrupt</c>.
    /// </summary>
    Interrupt,

    /// <summary>A routine that never returns: <c>noreturn</c>.</summary>
    NoReturn,

    /// <summary>
    /// The registers a routine returns with the values they had at entry: <c>keeps a, x</c>.
    /// </summary>
    Keeps,

    /// <summary>
    /// The registers whose values from its caller a routine uses: <c>reads a, c</c>, or
    /// <c>reads none</c>.
    /// </summary>
    Reads,

    /// <summary>
    /// A register that the store above a <c>.state</c> only saves, so the store is not a use of
    /// its value: <c>saves x</c>.
    /// </summary>
    Saves,

    /// <summary>
    /// Every tracked part of the state unknown, written as <c>?</c> on its own. A routine reached
    /// from outside nt65 assumes this, and an extern proc or an import usually declares it.
    /// </summary>
    AllUnknown,

    /// <summary>A signature set, which expands to the items it was declared with: <c>std</c>.</summary>
    Set,
}
