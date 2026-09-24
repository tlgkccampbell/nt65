namespace Norristown.Flow;

/// <summary>
/// Represents the cost of one pass through a routine, from where a call enters it to where its
/// path ends. A routine that loops has no upper bound, because the program does not say how many
/// times the loop runs. A routine containing an instruction with no count has neither bound.
/// </summary>
/// <param name="Least">The fewest cycles a path through it can take, or null when unknown.</param>
/// <param name="Most">
/// The most cycles a path through it can take, or null when the routine loops or the count is
/// unknown.
/// </param>
/// <param name="Calls">
/// Whether it calls. Here a call counts only the call instruction itself. The time the called
/// routine takes is that routine's own count, so a count along a path with a call is short by
/// that much.
/// </param>
/// <param name="Ends">
/// Whether a path through it ever leaves it. A routine that loops forever does not, and has no
/// pass to put a cost on at all.
/// </param>
/// <param name="Uncounted">
/// A sentence saying why there is no count, where an instruction nt65 knows takes a time that
/// only the running program decides. That instruction is a block move, whose length is in A. It
/// is null wherever the count is known, and wherever the uncounted line has already been
/// reported.
/// </param>
/// <param name="Excluded">
/// For a cost with calls, the items it leaves out because nt65 cannot count them, in the order
/// the routine reaches each. The cost is then a lower bound with no upper bound. It is empty or
/// null when nothing is left out.
/// </param>
public readonly record struct RoutineCost(
    int? Least, int? Most, bool Calls, bool Ends, string? Uncounted = null,
    IReadOnlyList<Exclusion>? Excluded = null)
{
    /// <summary>
    /// Gets a value indicating whether nt65 has a count for every instruction a path through the
    /// routine runs.
    /// </summary>
    public bool IsKnown => Least is not null;

    /// <summary>
    /// Gets a value indicating whether a path through the routine can come back on itself, so
    /// there is no upper bound.
    /// </summary>
    public bool Loops => Least is not null && Most is null;
}
