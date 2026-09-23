using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// What a routine costs with what it calls in it. A call costs the call itself and then
/// whatever the routine it names costs, so the two compose wherever nt65 can name the callee
/// and has its body; a tail jump and a <c>.fallthrough</c> into another routine compose the same
/// way, because control comes back from either to this routine's caller.
/// <para>
/// A call to a routine with no body, one through a pointer, and a routine that can reach
/// itself leave the total unknown. Saying so is the point: a total that quietly left a callee
/// out would read as a bound and not be one.
/// </para>
/// </summary>
public static class CallCosts
{
    /// <summary>Works out the total for every routine of <paramref name="flows"/> and writes it on each.</summary>
    public static void Compose(IEnumerable<ControlFlow> flows)
    {
        var regions = new Dictionary<(string Path, string Name), FlowRegion>();
        foreach (var flow in flows)
        {
            foreach (var region in flow.Regions)
                regions.TryAdd(Named(region.Routine), region);
        }
        var returns = Returning(regions);
        var totals = new Dictionary<(string Path, string Name), RoutineCost>();
        foreach (var region in regions.Values)
            region.Total = Total(region, regions, returns, totals, []);
    }

    /// <summary>
    /// Which routines control ever returns from. An exit from a routine is a reached block with
    /// no successor inside it: a return, a tail jump, a <c>.fallthrough</c> into another routine,
    /// or a call to a routine that never returns. A routine returns when any of its exits does:
    /// a return always does, a tail jump or a fall-through does only when the routine it goes on
    /// into returns, and a call that never returns does not.
    /// <para>
    /// A routine is assumed not to return until something shows that it does, and the set is
    /// recomputed until it stops changing, so a chain of tail jumps that ends in an infinite
    /// loop is found however long the chain is. A call nt65 cannot identify, and one to a
    /// routine with no body, are assumed to return, since saying they do not would be a claim
    /// about code that is not here.
    /// </para>
    /// </summary>
    private static HashSet<(string Path, string Name)> Returning(Dictionary<(string Path, string Name), FlowRegion> regions)
    {
        var found = new HashSet<(string Path, string Name)>();
        bool moved;
        do
        {
            moved = false;
            foreach (var (name, region) in regions)
            {
                if (!found.Contains(name) && ComesBack(region, regions, found))
                    moved |= found.Add(name);
            }
        }
        while (moved);
        return found;
    }

    /// <summary>Whether any exit from a routine is one through which control returns.</summary>
    private static bool ComesBack(
        FlowRegion region,
        Dictionary<(string Path, string Name), FlowRegion> regions,
        HashSet<(string Path, string Name)> found)
    {
        foreach (var block in region.Blocks)
        {
            if (!block.IsReached || block.Successors.Any(edge => edge.Kind != EdgeKind.Call))
                continue;
            var handsOff = Onward(block).Any(callee =>
                regions.ContainsKey(Named(callee)) && !found.Contains(Named(callee)));
            if (!handsOff)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A routine identified by its file and its flattened name, rather than by its symbol: an
    /// analysis that kept a file's results from before an edit holds a different symbol object
    /// for the same routine. It is the name and not the position, because a kept file still
    /// refers to the routines of an edited file at the positions they had before the edit.
    /// </summary>
    private static (string Path, string Name) Named(Symbol routine) => (routine.Tree.Path, routine.FlatName);

    /// <summary>
    /// What <paramref name="region"/>'s routine costs with its calls, working out what each
    /// callee costs first. <paramref name="walking"/> holds the routines being worked out, so
    /// that one reaching itself is found rather than followed round for ever.
    /// </summary>
    private static RoutineCost Total(
        FlowRegion region,
        Dictionary<(string Path, string Name), FlowRegion> regions,
        HashSet<(string Path, string Name)> returns,
        Dictionary<(string Path, string Name), RoutineCost> totals,
        HashSet<(string Path, string Name)> walking)
    {
        var name = Named(region.Routine);
        if (totals.TryGetValue(name, out var found))
            return found;
        if (!walking.Add(name))
            return new RoutineCost(null, null, true, true);

        // The fewest and the most are worked out separately, because a callee that loops has a
        // fewest but no most, and its caller then has a fewest but no most too.
        var (least, _, ends) = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, true));
        var most = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, false)).Most;
        walking.Remove(name);
        var cost = new RoutineCost(
            least, most, Calls(region), ends && returns.Contains(name), region.Cost.Uncounted);
        totals[name] = cost;
        return cost;

        CycleCount? Weighed(BasicBlock block, bool fewest)
        {
            if (Paths.Costing(block) is not { } own || block.CallsUnknown)
                return null;
            var with = fewest ? own.Least : own.Most;

            // A call inside a counted loop is made once per iteration, so its cost is counted
            // as many times as the loop runs.
            foreach (var callee in Onward(block))
            {
                var name = Named(callee);
                if (!regions.TryGetValue(name, out var called))
                    return null;

                // Control does not come back from a routine that never returns, so the pass
                // ends where it is called and what it does is no part of this one.
                if (!returns.Contains(name))
                    continue;
                var total = Total(called, regions, returns, totals, walking);
                if ((fewest ? total.Least : total.Most) is not { } cycles)
                    return null;
                with += cycles * (block.Turns ?? 1);
            }
            return new CycleCount(with);
        }
    }

    /// <summary>Whether a routine calls at all, which is what makes a total differ from its own cost.</summary>
    private static bool Calls(FlowRegion region) =>
        region.Blocks.Any(block => Onward(block).Any() || block.CallsUnknown);

    /// <summary>
    /// The routines a block hands control to and whose cost is then part of this one's: those
    /// it calls or tail-jumps to, and the one its <c>.fallthrough</c> runs on into.
    /// </summary>
    private static IEnumerable<Symbol> Onward(BasicBlock block) =>
        block.RunsInto is { } into ? block.Calls.Append(into) : block.Calls;
}
