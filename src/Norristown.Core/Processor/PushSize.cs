namespace Norristown.Processor;

/// <summary>
/// Specifies how much of the stack one push takes. The 65816 pushes a register at the
/// register's current width, so a push and its matching pull agree only when the width is the
/// same at both. For that reason, this names the register whose width applies rather than
/// giving a count of bytes.
/// </summary>
public enum PushSize
{
    /// <summary>One byte at any register width, as for <c>php</c>, <c>phb</c> and <c>phk</c>.</summary>
    OneByte,

    /// <summary>
    /// Two bytes at any register width, as for <c>phd</c>, <c>pea</c>, <c>pei</c> and <c>per</c>.
    /// </summary>
    TwoBytes,

    /// <summary>As wide as the accumulator, as for <c>pha</c>.</summary>
    Accumulator,

    /// <summary>As wide as the index registers, as for <c>phx</c> and <c>phy</c>.</summary>
    Index,
}
