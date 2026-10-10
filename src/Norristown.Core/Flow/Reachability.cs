namespace Norristown.Flow;

/// <summary>Finds which blocks of a routine a path from one of its blocks reaches.</summary>
internal static class Reachability
{
    /// <summary>
    /// Returns which of <paramref name="blocks"/> a path from the block at <paramref name="entry"/>
    /// reaches, the entry itself among them, following from each block only the blocks that
    /// <paramref name="onward"/> gives for it.
    /// </summary>
    public static bool[] From(IReadOnlyList<BasicBlock> blocks, int entry, Func<BasicBlock, IEnumerable<int>> onward)
    {
        var found = new bool[blocks.Count];
        var pending = new Queue<int>();
        found[entry] = true;
        pending.Enqueue(entry);
        while (pending.Count > 0)
        {
            foreach (var to in onward(blocks[pending.Dequeue()]))
            {
                if (found[to])
                    continue;
                found[to] = true;
                pending.Enqueue(to);
            }
        }
        return found;
    }
}
