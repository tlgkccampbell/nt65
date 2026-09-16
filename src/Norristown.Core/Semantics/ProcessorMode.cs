namespace Norristown.Semantics;

/// <summary>What the analysis knows about the emulation flag.</summary>
public enum ProcessorMode : byte
{
    /// <summary>Native mode, where the widths can be 16 bits.</summary>
    Native,

    /// <summary>Emulation mode, where both widths are pinned at 8 bits.</summary>
    Emulation,

    /// <summary>Not known here.</summary>
    Unknown,

    /// <summary>Whatever it was when the routine was entered.</summary>
    Unchanged,
}
