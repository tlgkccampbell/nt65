namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one routine in a call tree under a location.</summary>
/// <param name="Name">The routine's name.</param>
/// <param name="Declaration">Where its name is declared.</param>
/// <param name="Handler">Whether it is an interrupt handler.</param>
/// <param name="Interrupt">Whether it runs only in an interrupt.</param>
/// <param name="Unknown">Whether it reaches the location while D is not known.</param>
/// <param name="Role">
/// What it does with the location, which is <c>in</c>, <c>out</c>, <c>inout</c>, <c>temp</c>,
/// <c>read</c> or <c>write</c>. It is null for a routine that only leads to one that reaches it.
/// </param>
/// <param name="Via">The calls in the parent routine that reach this one, or none at the root.</param>
/// <param name="Accesses">Its own instructions that reach the location.</param>
/// <param name="Hazards">The facts that make up a hazard in it, or none.</param>
/// <param name="Children">The routines it calls that lead to one that reaches the location.</param>
internal sealed record DirectPageRoutine(
    string Name, Location Declaration, bool Handler, bool Interrupt, bool Unknown, string? Role, IReadOnlyList<Location> Via,
    IReadOnlyList<DirectPageAccess> Accesses, IReadOnlyList<DirectPageNote> Hazards, IReadOnlyList<DirectPageRoutine> Children);
