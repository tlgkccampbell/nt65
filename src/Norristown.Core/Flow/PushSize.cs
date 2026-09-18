namespace Norristown.Flow;

/// <summary>
/// How much of the stack one push takes. The 65816 pushes a register as wide as the register
/// is, so a save and its restore match only when the width is the same at both, which is what
/// keeps this apart from a count of bytes.
/// </summary>
public enum PushSize
{
    /// <summary>One byte, whatever the widths are: <c>php</c>, <c>phb</c>, <c>phk</c>.</summary>
    OneByte,

    /// <summary>Two bytes, whatever the widths are: <c>phd</c>, <c>pea</c>, <c>pei</c>, <c>per</c>.</summary>
    TwoBytes,

    /// <summary>As wide as the accumulator: <c>pha</c>.</summary>
    Accumulator,

    /// <summary>As wide as the index registers: <c>phx</c>, <c>phy</c>.</summary>
    Index,
}
