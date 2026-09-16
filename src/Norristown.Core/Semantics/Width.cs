namespace Norristown.Semantics;

/// <summary>What the analysis knows about how wide a register is.</summary>
public enum Width : byte
{
    /// <summary>Eight bits.</summary>
    Eight,

    /// <summary>Sixteen bits.</summary>
    Sixteen,

    /// <summary>Not known here.</summary>
    Unknown,

    /// <summary>
    /// Whatever it was when the routine was entered, which a <c>*</c> item promises to hand
    /// back. Where a known width is needed this counts as unknown.
    /// </summary>
    Unchanged,
}
