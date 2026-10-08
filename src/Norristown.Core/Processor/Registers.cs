namespace Norristown.Processor;

/// <summary>
/// Specifies the registers a routine preserves, reads or destroys. These are the three that hold
/// values, and the carry, zero, negative and overflow flags. A flag is followed as a register is,
/// so a routine that leaves a flag as its caller set it keeps that flag.
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

    /// <summary>The zero flag.</summary>
    Z = 16,

    /// <summary>The negative flag.</summary>
    N = 32,

    /// <summary>The overflow flag.</summary>
    V = 64,

    /// <summary>The flags that a <c>php</c> saves and that are followed as registers.</summary>
    Flags = C | Z | N | V,

    /// <summary>Every register.</summary>
    All = A | X | Y | Flags,
}
