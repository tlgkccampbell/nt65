namespace Norristown.Semantics;

/// <summary>
/// Specifies a set of the parts of the 65816's state that a signature can declare. A part a
/// routine declares is a contract, checked at its calls and its returns. A part it leaves out is
/// inferred: its exit from the routine's body, and its entry from what the routine's callers
/// agree on.
/// </summary>
[Flags]
public enum StateParts
{
    /// <summary>No part.</summary>
    None = 0,

    /// <summary>The accumulator's width.</summary>
    A = 1,

    /// <summary>The index registers' width.</summary>
    Index = 2,

    /// <summary>The emulation flag.</summary>
    Mode = 4,

    /// <summary>The direct page.</summary>
    DirectPage = 8,

    /// <summary>The data bank.</summary>
    DataBank = 16,

    /// <summary>Every part.</summary>
    All = A | Index | Mode | DirectPage | DataBank,
}
