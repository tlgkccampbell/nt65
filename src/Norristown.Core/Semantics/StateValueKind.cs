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
}
