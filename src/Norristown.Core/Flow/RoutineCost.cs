namespace Norristown.Flow;

/// <summary>
/// What one pass through a routine costs, from where a call enters it to where its path
/// ends. A routine that loops has no maximum, because the program does not say how many times
/// the loop runs, and one holding an instruction with no count has neither bound.
/// </summary>
/// <param name="Least">The fewest cycles a path through it can take, or null when unknown.</param>
/// <param name="Most">The most, or null when the routine loops or the count is unknown.</param>
/// <param name="Calls">
/// Whether it calls. Here a call counts only the call instruction itself; what the called
/// routine takes is that routine's own count, so a count along a path with a call is short by
/// that much.
/// </param>
/// <param name="Ends">
/// Whether a path through it ever leaves it. A routine that loops for ever does not, and has
/// no pass to put a cost on at all.
/// </param>
/// <param name="Uncounted">
/// Why there is no count, in a sentence, where an instruction nt65 knows takes a time that
/// only the running program decides: a block move, whose length is in A. Null wherever the
/// count is known, and wherever the uncounted line has already been reported.
/// </param>
public readonly record struct RoutineCost(
    int? Least, int? Most, bool Calls, bool Ends, string? Uncounted = null)
{
    /// <summary>Whether nt65 has a count for every instruction a path through it runs.</summary>
    public bool IsKnown => Least is not null;

    /// <summary>Whether a path through it can come back on itself, so there is no most.</summary>
    public bool Loops => Least is not null && Most is null;
}
