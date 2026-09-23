namespace Norristown.Processor;

/// <summary>
/// How much of the stack one push takes. The 65816 pushes a register at the register's current
/// width, so a push and its matching pull agree only when the width is the same at both, which
/// is why this names the register's width rather than giving a count of bytes.
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
