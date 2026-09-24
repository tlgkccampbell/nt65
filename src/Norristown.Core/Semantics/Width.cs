namespace Norristown.Semantics;

/// <summary>Specifies what the analysis knows about how wide a register is.</summary>
public enum Width : byte
{
    /// <summary>Eight bits.</summary>
    Eight,

    /// <summary>Sixteen bits.</summary>
    Sixteen,

    /// <summary>Not known at this point.</summary>
    Unknown,

    /// <summary>
    /// The same as when the routine was entered, which a <c>*</c> item promises to return
    /// unchanged. Where a known width is needed, this counts as unknown.
    /// </summary>
    Unchanged,
}
