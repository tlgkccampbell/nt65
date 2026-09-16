namespace Norristown.Semantics;

/// <summary>Which part of the processor state a state item is about.</summary>
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

    /// <summary>Data written after each call: <c>inline n</c>.</summary>
    Inline,

    /// <summary>The direct page: <c>dp = e</c>, <c>dp?</c>, <c>dp*</c>.</summary>
    DirectPage,

    /// <summary>The data bank: <c>dbr = e</c>, <c>dbr?</c>, <c>dbr*</c>.</summary>
    DataBank,
}
