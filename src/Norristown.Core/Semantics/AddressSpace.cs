namespace Norristown.Semantics;

/// <summary>
/// An address space other than the host's: the memory another processor runs in, such as the
/// SNES sound CPU's or a disk drive's. A segment that names a space is in it; every other
/// segment is in the host's. An address in one space is only a number to code in another
/// space: that code may use its value but can never jump to it or read or write through it.
/// </summary>
/// <param name="Name">The name segments refer to it by, as <c>space = name</c>.</param>
/// <param name="HoldsCode">
/// Whether the program's own processor runs in this space, so its segments may hold routines
/// that nt65 checks; otherwise the code in it belongs to another processor and is written as
/// data and macro calls.
/// </param>
/// <param name="Declaration">Where it is declared.</param>
public sealed record AddressSpace(string Name, bool HoldsCode, Span Declaration)
{
    /// <summary>How a diagnostic names a space: <c>space `spc`</c>, or "the host's space" for null.</summary>
    public static string Spell(string? space) => space is null ? "the host's space" : $"space `{space}`";
}
