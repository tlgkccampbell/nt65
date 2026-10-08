using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>Represents what one routine does with one location on a <see cref="DirectPage"/>.</summary>
/// <param name="Routine">The routine.</param>
/// <param name="Role">What the routine's own instructions do with the location.</param>
/// <param name="Accesses">The routine's instructions that reach the location, in the order they are emitted.</param>
/// <param name="Hazards">The facts that make up a hazard in this routine, or none.</param>
/// <param name="IsHandler">Whether the routine is an interrupt handler.</param>
/// <param name="InInterrupt">
/// Whether the routine runs in an interrupt, as a handler or a routine a handler reaches through its calls.
/// The routine may run outside interrupts as well.
/// </param>
/// <param name="InMain">
/// Whether the routine runs outside interrupts, reached from where the rest of the program starts.
/// A routine that neither the program's starts nor a handler reaches is taken to run outside them.
/// </param>
/// <param name="IsUnknownPage">
/// Whether the routine reaches the location through the direct page while D is not known, so that
/// it may reach some other address instead.
/// </param>
public sealed record PageUse(
    Symbol Routine, PageRole Role, IReadOnlyList<PageAccess> Accesses, IReadOnlyList<PageNote> Hazards, bool IsHandler, bool InInterrupt, bool InMain,
    bool IsUnknownPage = false);
