namespace Norristown.Semantics;

/// <summary>Specifies how much the analysis knows about a register it follows by value.</summary>
public enum StateValueKind : byte
{
    /// <summary>
    /// The same as when the routine was entered, which a <c>*</c> item promises to return
    /// unchanged.
    /// </summary>
    Unchanged,

    /// <summary>Not known at this point.</summary>
    Unknown,

    /// <summary>A known value.</summary>
    Known,

    /// <summary>
    /// One of a known set of values, which only the data bank can have:
    /// <c>dbr = [$00..$3f, $80..$bf]</c>.
    /// </summary>
    Among,

    /// <summary>
    /// The same as when the routine was entered, which is one of a known set. This is the data
    /// bank of a routine declared with <c>dbr = [...]</c>, which it returns unchanged.
    /// </summary>
    Within,
}
