namespace Norristown.Flow;

/// <summary>
/// Represents the scratch a routine uses, which <c>.scratch</c> declares, directly or through the
/// routines it calls. An editor shows it beside a routine's name.
/// </summary>
/// <param name="Reads">The scratch the routine reads before storing to it, which its callers set, by name.</param>
/// <param name="Stores">The scratch the routine may store to, which its callers cannot rely on afterwards, by name.</param>
public sealed record ScratchUse(IReadOnlyList<string> Reads, IReadOnlyList<string> Stores);
