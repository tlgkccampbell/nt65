using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// What a routine costs with what it calls in it. A call costs the call itself and then
/// whatever the routine it names costs, so the two compose wherever nt65 can name the callee
/// and has its body; a tail jump composes the same way, because control comes back from it to
/// this routine's caller.
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
    /// Which routines control ever comes back out of. A way out of a routine is a block
    /// nothing follows: a return, a tail jump, or a call to a routine that never comes back.
    /// The first two bring control back and the last does not, so a routine comes back when
    /// any way out of it does, and what its tail jump reaches has to come back for that.
    /// <para>
    /// A routine is taken not to come back until something shows that it does, and the answer
    /// is worked over again until it stops moving: a chain of tail jumps that ends in a loop
    /// for ever is then found however long the chain is. A call nt65 cannot name, and one to a
    /// routine with no body, are taken to come back, since saying they do not would be a claim
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

    /// <summary>Whether any way out of a routine is one control comes back through.</summary>
    private static bool ComesBack(
        FlowRegion region,
        Dictionary<(string Path, string Name), FlowRegion> regions,
        HashSet<(string Path, string Name)> found)
    {
        foreach (var block in region.Blocks)
        {
            if (!block.IsReached || block.Successors.Any(edge => edge.Kind != EdgeKind.Call))
                continue;
            var handsOff = block.Calls.Any(callee =>
                regions.ContainsKey(Named(callee)) && !found.Contains(Named(callee)));
            if (!handsOff)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A routine by its file and its flattened name, rather than by the symbol, which an
    /// analysis that kept the file before an edit holds a different object for. It is the name
    /// and not the position, because a file that was kept still names the routines of a file
    /// that changed at the positions they were at before the edit moved them.
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

        // The fewest and the most are worked out apart, because a callee that loops has a
        // fewest and no most, and that leaves its caller the same way round.
        var (least, _, ends) = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, true));
        var most = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, false)).Most;
        walking.Remove(name);
        var cost = new RoutineCost(least, most, Calls(region), ends && returns.Contains(name));
        totals[name] = cost;
        return cost;

        CycleCount? Weighed(BasicBlock block, bool fewest)
        {
            if (Paths.Costing(block) is not { } own || block.CallsUnknown)
                return null;
            var with = fewest ? own.Least : own.Most;

            // A call inside a counted loop is made once a turn, so what it costs counts as
            // many times over as the loop runs.
            foreach (var callee in block.Calls)
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
        region.Blocks.Any(block => block.Calls.Count > 0 || block.CallsUnknown);
}
