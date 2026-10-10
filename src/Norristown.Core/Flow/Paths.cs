using Norristown.Layout;

namespace Norristown.Flow;

/// <summary>
/// Works out what a path through a routine, or through part of one, costs. The lower bound is a
/// shortest path, which every graph has, since no block costs less than nothing. The upper bound
/// is a longest path, which only a graph with no cycle in it has.
/// <para>
/// A block that ends in a conditional branch is charged by the way a path leaves it. Falling
/// through pays the branch's least, and the taken edge pays from one more up to its most, so each
/// bound is the cost of a path the code has.
/// </para>
/// <para>
/// A call's edge is not followed. Control comes back to the statement after the call, which is
/// the fall-through edge already there, so following the call would walk into the callee and
/// never come out. What a call costs is put on the block that makes it instead, by the weight
/// the caller hands in.
/// </para>
/// </summary>
internal static class Paths
{
    /// <summary>
    /// Returns what one pass through a whole routine costs, with each block weighted by
    /// <see cref="Costing"/>.
    /// </summary>
    public static (int? Minimum, int? Maximum, bool Ends) Through(IReadOnlyList<BasicBlock> blocks) =>
        Through(blocks, 0, _ => true, Costing);

    /// <summary>
    /// Returns what one pass through part of a routine costs, starting from
    /// <paramref name="entry"/> and crossing the blocks <paramref name="inside"/> accepts, with
    /// <paramref name="weight"/> giving what each block costs. A path ends where nothing follows it
    /// or where what follows is outside.
    /// </summary>
    public static (int? Minimum, int? Maximum, bool Ends) Through(
        IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside, Func<BasicBlock, CycleCount?> weight)
    {
        if (entry < 0 || entry >= blocks.Count || !inside(entry))
            return (null, null, false);
        var reached = Reached(blocks, entry, inside);

        // Whether a path leaves at all is asked first, and on its own. A routine that loops
        // forever has no pass to cost, which is a different answer from a pass nt65 cannot count.
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
        return (Minimum(blocks, entry, inside, weight), Maximum(blocks, reached, inside, weight), true);
    }

    /// <summary>
    /// Returns what a block costs. That is its own cycles where a pass runs it once. For a block in
    /// a counted loop it is the block's share of the whole loop's cost, which is all of it on the
    /// header and none on the rest. A counted loop's back edge is not followed, so it no longer
    /// makes a path cycle.
    /// </summary>
    public static CycleCount? Costing(BasicBlock block) => block.LoopCycles ?? block.Cycles;

    /// <summary>Returns which blocks a path from <paramref name="entry"/> can reach without leaving.</summary>
    private static bool[] Reached(IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside) =>
        Reachability.From(blocks, entry, block => Onward(block, inside));

    /// <summary>
    /// Returns the blocks a path may run after this one, which are the blocks that
    /// <see cref="Edges"/> reaches.
    /// </summary>
    private static IEnumerable<int> Onward(BasicBlock block, Func<int, bool> inside) =>
        Edges(block, inside).Select(edge => edge.To).Distinct();

    /// <summary>
    /// Returns whether a path that reaches a block may end there, because nothing follows it, what
    /// follows it is outside, or it branches out of the routine.
    /// </summary>
    private static bool Leaves(BasicBlock block, Func<int, bool> inside) =>
        block.BranchesOut
        || block.Successors.Any(edge => edge.Kind != EdgeKind.Call && !inside(edge.To))
        || !block.Successors.Any(edge => edge.Kind != EdgeKind.Call);

    /// <summary>
    /// Returns what a path pays for a block where it leaves the block by the branch that ends it,
    /// when <paramref name="taken"/> is true, or by falling through. A block that does not end in a
    /// conditional branch costs its <paramref name="weight"/> either way. A block of a counted loop
    /// does too, because the loop's cost already charges its branches.
    /// </summary>
    private static CycleCount Charged(BasicBlock block, CycleCount weight, bool taken)
    {
        if (block.LoopCycles is not null || block.Taken is not { } yes || block.NotTaken is not { } no)
            return weight;

        // The weight holds the branch's whole interval, from its least not taken to its most
        // taken, and the way the path leaves pays only that way's share of it.
        var paid = taken ? yes : no;
        return new CycleCount(
            weight.Minimum + paid.Minimum - no.Minimum,
            weight.Maximum + paid.Maximum - yes.Maximum);
    }

    /// <summary>
    /// Returns the edges a path may follow from a block, as the block reached and whether the
    /// block's branch is taken to reach it. Call edges, edges out of the region and the back edge
    /// of a counted loop are left out.
    /// </summary>
    private static IEnumerable<(int To, bool Taken)> Edges(BasicBlock block, Func<int, bool> inside) =>
        block.Successors
            .Where(edge => edge.Kind != EdgeKind.Call && inside(edge.To) && edge.To != block.Repeats)
            .Select(edge => (edge.To, edge.Kind != EdgeKind.FallThrough))
            .Distinct();

    /// <summary>
    /// Returns the ways a path may end at a block, as whether the block's branch is taken on each.
    /// They are the ways <see cref="Leaves"/> finds.
    /// </summary>
    private static IEnumerable<bool> Exits(BasicBlock block, Func<int, bool> inside)
    {
        if (block.BranchesOut)
            yield return true;
        var onward = false;
        foreach (var edge in block.Successors.Where(edge => edge.Kind != EdgeKind.Call))
        {
            onward = true;
            if (!inside(edge.To))
                yield return edge.Kind != EdgeKind.FallThrough;
        }

        // A block that nothing follows ends the path where its last statement leaves. Only a
        // branch proved never taken runs on past its end into nothing.
        if (!onward)
            yield return block.End != BlockEnd.Through;
    }

    /// <summary>
    /// Returns the cost of the shortest path to an end. No block has a negative cost, so this works
    /// for any arrangement of blocks, including one that forms loops. The cost of reaching a block
    /// excludes the block itself, which is paid on the edge that leaves it.
    /// </summary>
    private static int? Minimum(
        IReadOnlyList<BasicBlock> blocks, int entry, Func<int, bool> inside, Func<BasicBlock, CycleCount?> weight)
    {
        var best = new int?[blocks.Count];
        var pending = new PriorityQueue<int, int>();
        best[entry] = 0;
        pending.Enqueue(entry, 0);
        int? minimum = null;
        while (pending.TryDequeue(out var at, out var cost))
        {
            if (cost > best[at])
                continue;
            var block = blocks[at];
            var own = weight(block)!.Value;
            foreach (var taken in Exits(block, inside))
            {
                var end = cost + Charged(block, own, taken).Minimum;
                minimum = minimum is { } found ? Math.Min(found, end) : end;
            }
            foreach (var (to, taken) in Edges(block, inside))
            {
                var through = cost + Charged(block, own, taken).Minimum;
                if (best[to] is null || through < best[to])
                {
                    best[to] = through;
                    pending.Enqueue(to, through);
                }
            }
        }
        return minimum;
    }

    /// <summary>
    /// Returns the cost of the longest path to an end, or null when a path can come back on
    /// itself. The blocks are put in the order they can run in, and a cycle is what is left over
    /// when nothing can be put next. The cost of reaching a block excludes the block itself, which
    /// is paid on the edge that leaves it.
    /// </summary>
    private static int? Maximum(
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
                high[i] = 0;
                pending.Enqueue(i);
            }
        }
        var visited = 0;
        int? maximum = null;
        while (pending.Count > 0)
        {
            var at = pending.Dequeue();
            visited++;
            var block = blocks[at];
            var own = weight(block)!.Value;
            if (high[at] is { } from)
            {
                foreach (var taken in Exits(block, inside))
                {
                    var end = from + Charged(block, own, taken).Maximum;
                    maximum = maximum is { } found ? Math.Max(found, end) : end;
                }
                foreach (var (to, taken) in Edges(block, inside))
                {
                    var through = from + Charged(block, own, taken).Maximum;
                    if (high[to] is null || through > high[to])
                        high[to] = through;
                }
            }
            foreach (var to in Onward(block, inside))
            {
                if (--waiting[to] == 0)
                    pending.Enqueue(to);
            }
        }
        return visited == total ? maximum : null;
    }
}
