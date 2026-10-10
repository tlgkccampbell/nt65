using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>Represents what one routine does with one location of a <see cref="DataMap"/>.</summary>
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
/// <param name="IsNested">
/// Whether the routine relies on the location across a call to a routine that uses it as a
/// temporary of its own, which is the relation <see cref="DataRelation.Nested"/> names. The
/// notes in <see cref="Hazards"/> say which call and which routine.
/// </param>
public sealed record DataUse(
    Symbol Routine, DataRole Role, IReadOnlyList<DataAccess> Accesses, IReadOnlyList<DataNote> Hazards, bool IsHandler, bool InInterrupt, bool InMain,
    bool IsUnknownPage = false, bool IsNested = false);
