namespace Norristown.Processor;

/// <summary>
/// Specifies the registers a routine preserves or destroys, which are the three that hold
/// values, and the carry. N and Z are left out because nearly every instruction writes one of
/// them, so a promise about either would say nothing.
/// </summary>
[Flags]
public enum Registers
{
    /// <summary>No register at all.</summary>
    None = 0,

    /// <summary>The accumulator.</summary>
    A = 1,

    /// <summary>The X index register.</summary>
    X = 2,

    /// <summary>The Y index register.</summary>
    Y = 4,

    /// <summary>The carry flag.</summary>
    C = 8,

    /// <summary>Every register.</summary>
    All = A | X | Y | C,
}
