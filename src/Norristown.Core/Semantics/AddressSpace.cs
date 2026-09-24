namespace Norristown.Semantics;

/// <summary>
/// Represents an address space other than the host's, such as the memory of the SNES sound CPU
/// or of a disk drive. A segment that names a space is in that space, and every other segment
/// is in the host's space. To code in another space, an address is only a number: that code
/// may use its value, but it can never jump to the address or read or write through it.
/// </summary>
/// <param name="Name">The name that segments use to refer to the space, as <c>space = name</c>.</param>
/// <param name="HoldsCode">
/// Whether the program's own processor runs in this space, so that its segments may hold
/// routines that nt65 checks. Otherwise the code in the space belongs to another processor and
/// is expressed as data and macro calls.
/// </param>
/// <param name="Declaration">The span where the space is declared.</param>
public sealed record AddressSpace(string Name, bool HoldsCode, Span Declaration)
{
    /// <summary>
    /// Returns the name a diagnostic uses for a space, such as <c>space `spc`</c>, or "the host's
    /// space" when <paramref name="space"/> is null.
    /// </summary>
    public static string Format(string? space) => space is null ? "the host's space" : $"space `{space}`";
}
