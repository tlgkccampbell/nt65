using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents how an inline <c>.scope</c> lies over its routine's basic blocks, for a scope
/// with a single pass through it to ask about. Such a scope either lies inside one basic block
/// or is made up of whole basic blocks. Its cost and the registers it keeps are both worked out
/// only for these shapes.
/// </summary>
/// <param name="Entry">The index of the first block the scope is in.</param>
/// <param name="Inside">Which of the routine's blocks hold some of the scope.</param>
/// <param name="IsStraight">
/// Whether the scope lies inside the one block at <paramref name="Entry"/>, which runs all of it.
/// </param>
internal sealed record ScopeShape(int Entry, bool[] Inside, bool IsStraight)
{
    /// <summary>
    /// Returns how the part of a routine inside <paramref name="whole"/> lies over
    /// <paramref name="blocks"/>. It returns null where there is no single pass through that
    /// part, such as when a basic block holds part of it and part of something else, and where
    /// nothing reaches the block it starts in. It also returns null for a scope of whole blocks
    /// that takes in every block of the routine.
    /// </summary>
    public static ScopeShape? Of(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        var held = Held(blocks, whole);
        var inside = new bool[blocks.Count];
        var part = 0;
        var all = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            inside[i] = held[i].Inside > 0;
            if (held[i].Inside == 0)
                continue;
            if (held[i].Inside == held[i].Total)
                all++;
            else
                part++;
        }

        var entry = Array.IndexOf(inside, true);
        if (part == 1 && all == 0)
            return blocks[entry].IsReached ? new ScopeShape(entry, inside, true) : null;
        if (part > 0 || all == 0)
            return null;
        if (!blocks[entry].IsReached || inside.All(held => held))
            return null;
        return new ScopeShape(entry, inside, false);
    }

    /// <summary>
    /// Returns how many of each block's statements lie inside <paramref name="whole"/>, and how
    /// many it has. A statement in this file decides by its position whether the walk is inside
    /// the span. A statement from an expansion counts as being wherever the call that expanded it
    /// was, so the walk keeps the last answer over it.
    /// </summary>
    private static (int Inside, int Total)[] Held(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        var held = new (int Inside, int Total)[blocks.Count];
        var within = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            var inside = 0;
            foreach (var step in blocks[i].Steps)
            {
                if (step.On is null)
                    within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
                if (within)
                    inside++;
            }
            held[i] = (inside, blocks[i].Steps.Count);
        }
        return held;
    }
}
