namespace Norristown.Semantics;

/// <summary>
/// An address space other than the host's: the memory another processor runs in, such as the
/// SNES sound CPU's or a disk drive's. A segment that names one is in it, and every other
/// segment is in the host's. What is in one space is a value to code in another, never a
/// place it can jump to or reach.
/// </summary>
/// <param name="Name">The name segments give it as <c>space = name</c>.</param>
/// <param name="HoldsCode">
/// Whether it runs the program's own processor, so its segments may hold routines that nt65
/// checks; otherwise its code is another processor's, written as data and macro calls.
/// </param>
/// <param name="Declaration">Where it is declared.</param>
public sealed record AddressSpace(string Name, bool HoldsCode, Span Declaration)
{
    /// <summary>The space as a message names it: <c>space `spc`</c>, or the host's for none.</summary>
    public static string Spell(string? space) => space is null ? "the host's space" : $"space `{space}`";
}
