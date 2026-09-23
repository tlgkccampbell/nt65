using Norristown.Layout;

namespace Norristown.Flow;

/// <summary>
/// What a path through a routine, or through part of one, costs. The fewest cycles is a
/// shortest path, which every graph has, since no block costs less than nothing; the most is a
/// longest path, which only a graph with no cycle in it has.
/// <para>
/// A call's edge is not followed: control comes back to the statement after the call, which is
/// the fall-through edge already there, so following the call would walk into the callee and
/// never come out. What a call costs is put on the block that makes it instead, by the weight
/// the caller hands in.
/// </para>
/// </summary>
internal static class Paths
{
    /// <summary>What one pass through a whole routine costs, each block costing its own cycles.</summary>
    public static (int? Least, int? Most, bool Ends) Through(IReadOnlyList<BasicBlock> blocks) =>
        Through(blocks, 0, _ => true, Costing);

    /// <summary>
    /// What a block costs: its own cycles where a pass runs it once, or, for a block in a
    /// counted loop, its share of the whole loop's cost (all of it on the header, none on the
    /// rest). A counted loop's back edge is not followed, so it no longer makes a path cycle.
    /// </summary>
    public static CycleCount? Costing(BasicBlock block) => block.LoopCycles ?? block.Cycles;

    /// <summary>
    /// The same over part of a routine: from <paramref name="entry"/>, across the blocks
    /// <paramref name="inside"/> accepts, with <paramref name="weight"/> saying what each costs.
    /// A path ends where nothing follows it or where what follows is outside.
    /// </summary>
    public static (int? Least, int? Most, bool Ends) Through(
        IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside, Func<BasicBlock, CycleCount?> weight)
    {
        if (entry < 0 || entry >= blocks.Count || !inside(entry))
            return (null, null, false);
        var reached = Reached(blocks, entry, inside);

        // Whether a path leaves at all is asked first, and on its own: a routine that loops
        // for ever has no pass to cost, which is a different answer from one nt65 cannot count.
        var ends = false;
        for (var i = 0; i < blocks.Count; i++)
            ends |= reached[i] && Leaves(blocks[i], inside);
        if (!ends)
            return (null, null, false);

        // If any reachable block has no count, the whole pass has none, just as one uncounted
        // instruction leaves its block without a count.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (reached[i] && weight(blocks[i]) is null)
                return (null, null, true);
        }
        return (Least(blocks, entry, inside, weight), Most(blocks, reached, inside, weight), true);
    }

    /// <summary>Which blocks a path from <paramref name="entry"/> can reach without leaving.</summary>
    private static bool[] Reached(IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside)
    {
        var found = new bool[blocks.Count];
        var pending = new Queue<int>();
        found[entry] = true;
        pending.Enqueue(entry);
        while (pending.Count > 0)
        {
            foreach (var to in Onward(blocks[pending.Dequeue()], inside))
            {
                if (found[to])
                    continue;
                found[to] = true;
                pending.Enqueue(to);
            }
        }
        return found;
    }

    /// <summary>
    /// The blocks a path may run after this one, ignoring call edges, edges out of the region,
    /// and the back edge of a counted loop.
    /// </summary>
    private static IEnumerable<int> Onward(BasicBlock block, Func<int, bool> inside) =>
        block.Successors
            .Where(edge => edge.Kind != EdgeKind.Call && inside(edge.To) && edge.To != block.Repeats)
            .Select(edge => edge.To)
            .Distinct();

    /// <summary>Whether a path that reaches a block may end there: nothing follows it, or what does is outside.</summary>
    private static bool Leaves(BasicBlock block, Func<int, bool> inside) =>
        block.Successors.Any(edge => edge.Kind != EdgeKind.Call && !inside(edge.To))
        || !block.Successors.Any(edge => edge.Kind != EdgeKind.Call);

    /// <summary>
    /// The shortest path to where it ends. No block has a negative cost, so this works however
    /// the blocks are joined, including when they form loops.
    /// </summary>
    private static int? Least(
        IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside, Func<BasicBlock, CycleCount?> weight)
    {
        var best = new int?[blocks.Count];
        var pending = new PriorityQueue<int, int>();
        best[entry] = weight(blocks[entry])!.Value.Least;
        pending.Enqueue(entry, best[entry]!.Value);
        int? least = null;
        while (pending.TryDequeue(out var at, out var cost))
        {
            if (cost > best[at])
                continue;
            if (Leaves(blocks[at], inside))
                least = least is { } found ? Math.Min(found, cost) : cost;
            foreach (var to in Onward(blocks[at], inside))
            {
                var through = cost + weight(blocks[to])!.Value.Least;
                if (best[to] is null || through < best[to])
                {
                    best[to] = through;
                    pending.Enqueue(to, through);
                }
            }
        }
        return least;
    }

    /// <summary>
    /// The longest path to where it ends, or null when a path can come back on itself: the
    /// blocks are put in the order they can run in, and a cycle is what is left over when
    /// nothing can be put next.
    /// </summary>
    private static int? Most(
        IReadOnlyList<BasicBlock> blocks, bool[] reached, Func<int, bool> inside, Func<BasicBlock, CycleCount?> weight)
    {
        var waiting = new int[blocks.Count];
        var total = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!reached[i])
                continue;
            total++;
            foreach (var to in Onward(blocks[i], inside))
                waiting[to]++;
        }

        var high = new int?[blocks.Count];
        var pending = new Queue<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (reached[i] && waiting[i] == 0)
            {
                high[i] = weight(blocks[i])!.Value.Most;
                pending.Enqueue(i);
            }
        }
        var placed = 0;
        int? most = null;
        while (pending.Count > 0)
        {
            var at = pending.Dequeue();
            placed++;
            if (Leaves(blocks[at], inside) && high[at] is { } end)
                most = most is { } found ? Math.Max(found, end) : end;
            foreach (var to in Onward(blocks[at], inside))
            {
                if (high[at] is { } from)
                {
                    var through = from + weight(blocks[to])!.Value.Most;
                    if (high[to] is null || through > high[to])
                        high[to] = through;
                }
                if (--waiting[to] == 0)
                    pending.Enqueue(to);
            }
        }
        return placed == total ? most : null;
    }
}
