namespace Norristown.Flow;

/// <summary>
/// The loops in a routine: the sets of blocks a path can cycle through. A loop is found from a
/// back edge, which is an edge to a block (the header) that lies on every path from the entry
/// to the edge's source (the latch). The loop holds the header and every block that can reach
/// the latch without passing through the header.
/// </summary>
internal static class Loops
{
    /// <summary>
    /// Every loop in <paramref name="blocks"/>, the innermost first, so that a loop inside
    /// another is settled before the cost of one iteration of the enclosing loop is worked out.
    /// </summary>
    public static List<Loop> In(IReadOnlyList<BasicBlock> blocks)
    {
        var found = new List<Loop>();
        if (blocks.Count == 0)
            return found;
        var over = Dominators(blocks);
        for (var latch = 0; latch < blocks.Count; latch++)
        {
            if (over[latch] is null)
                continue;
            foreach (var edge in blocks[latch].Successors)
            {
                // A back edge goes to a block that every path to this one has already run through.
                if (edge.Kind == EdgeKind.Call || !over[latch]![edge.To])
                    continue;
                found.Add(new Loop(edge.To, latch, Around(blocks, edge.To, latch)));
            }
        }
        found.Sort((first, second) => Held(first.Inside).CompareTo(Held(second.Inside)));
        return found;
    }

    /// <summary>
    /// How many blocks a loop holds. Sorting by this puts a loop inside another before the loop
    /// that contains it.
    /// </summary>
    private static int Held(bool[] inside) => inside.Count(held => held);

    /// <summary>
    /// Which blocks lie on every path from the routine's entry to each block (its dominators),
    /// or null for a block no path reaches. Every reached block but the entry starts out
    /// dominated by all blocks, and the sets are recomputed until they stop shrinking.
    /// </summary>
    private static bool[]?[] Dominators(IReadOnlyList<BasicBlock> blocks)
    {
        var reached = Reached(blocks);
        var over = new bool[]?[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!reached[i])
                continue;
            over[i] = new bool[blocks.Count];
            if (i == 0)
                over[i]![0] = true;
            else
                Array.Fill(over[i]!, true);
        }

        bool moved;
        do
        {
            moved = false;
            for (var i = 1; i < blocks.Count; i++)
            {
                if (over[i] is null)
                    continue;
                var mine = new bool[blocks.Count];
                var any = false;
                for (var from = 0; from < blocks.Count; from++)
                {
                    if (over[from] is null || !Leads(blocks[from], i))
                        continue;
                    if (!any)
                    {
                        Array.Copy(over[from]!, mine, blocks.Count);
                        any = true;
                    }
                    else
                    {
                        for (var at = 0; at < blocks.Count; at++)
                            mine[at] &= over[from]![at];
                    }
                }
                mine[i] = true;
                for (var at = 0; at < blocks.Count; at++)
                {
                    if (over[i]![at] != mine[at])
                    {
                        over[i]![at] = mine[at];
                        moved = true;
                    }
                }
            }
        }
        while (moved);
        return over;
    }

    /// <summary>Whether a path can run from one block straight into another, ignoring call edges.</summary>
    private static bool Leads(BasicBlock block, int to) =>
        block.Successors.Any(edge => edge.Kind != EdgeKind.Call && edge.To == to);

    /// <summary>Which blocks a path from the routine's entry reaches, ignoring call edges.</summary>
    private static bool[] Reached(IReadOnlyList<BasicBlock> blocks)
    {
        var found = new bool[blocks.Count];
        var pending = new Queue<int>();
        found[0] = true;
        pending.Enqueue(0);
        while (pending.Count > 0)
        {
            foreach (var edge in blocks[pending.Dequeue()].Successors)
            {
                if (edge.Kind == EdgeKind.Call || found[edge.To])
                    continue;
                found[edge.To] = true;
                pending.Enqueue(edge.To);
            }
        }
        return found;
    }

    /// <summary>
    /// The blocks a loop holds: its header, its latch, and everything the latch can be
    /// reached from without running through the header again.
    /// </summary>
    private static bool[] Around(IReadOnlyList<BasicBlock> blocks, int header, int latch)
    {
        var inside = new bool[blocks.Count];
        inside[header] = true;
        var pending = new Stack<int>();
        if (!inside[latch])
        {
            inside[latch] = true;
            pending.Push(latch);
        }
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            for (var from = 0; from < blocks.Count; from++)
            {
                if (inside[from] || !Leads(blocks[from], at))
                    continue;
                inside[from] = true;
                pending.Push(from);
            }
        }
        return inside;
    }
}
