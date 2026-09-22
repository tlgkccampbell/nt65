namespace Norristown.Semantics;

/// <summary>How much the analysis knows about a register it follows by value.</summary>
public enum StateValueKind : byte
{
    /// <summary>Whatever it was when the routine was entered, which a <c>*</c> item promises to hand back.</summary>
    Unchanged,

    /// <summary>Not known here.</summary>
    Unknown,

    /// <summary>A known value.</summary>
    Known,

    /// <summary>One of a known set of values, which only the data bank is given: <c>dbr = [$00..$3f, $80..$bf]</c>.</summary>
    Among,

    /// <summary>
    /// Whatever it was when the routine was entered, which is one of a known set: the data bank of
    /// a routine declared <c>dbr = [...]</c>, which it hands back as it found it.
    /// </summary>
    Within,
}
