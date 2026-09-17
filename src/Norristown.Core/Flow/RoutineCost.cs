namespace Norristown.Flow;

/// <summary>
/// What one pass through a routine costs, from where a call enters it to where its path
/// ends. A routine that loops has no most, because how many turns it takes is not in the
/// program, and one holding an instruction with no count has neither.
/// </summary>
/// <param name="Least">The fewest cycles a path through it can take, or null when unknown.</param>
/// <param name="Most">The most, or null when the routine loops or the count is unknown.</param>
/// <param name="Calls">
/// Whether it calls. A call costs the call itself here, and what the routine it names does
/// is that routine's own count, so a count that meets a call is short by what the call does.
/// </param>
/// <param name="Ends">
/// Whether a path through it ever leaves it. A routine that loops for ever does not, and has
/// no pass to put a cost on at all.
/// </param>
public readonly record struct RoutineCost(int? Least, int? Most, bool Calls, bool Ends)
{
    /// <summary>Whether nt65 has a count for every instruction a path through it runs.</summary>
    public bool IsKnown => Least is not null;

    /// <summary>Whether a path through it can come back on itself, so there is no most.</summary>
    public bool Loops => Least is not null && Most is null;
}
