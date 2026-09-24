using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Works out what a routine costs including the routines it calls. A call costs the call
/// instruction itself plus what the routine it names costs, so the two compose wherever nt65 can
/// name the callee and has its body. A tail jump and a <c>.fallthrough</c> into another routine
/// compose the same way, because control comes back from either to this routine's caller.
/// <para>
/// A call to a routine with no body, a call through a pointer, and a routine that can reach
/// itself cannot be counted. The total counts everything else, so it is a lower bound with no
/// upper bound, and it lists each item it left out. The list matters because a total that
/// silently omitted a callee would look like a bound when it is not one.
/// </para>
/// </summary>
public static class CallCosts
{
    /// <summary>
    /// Works out the total cost of every routine of <paramref name="flows"/> and stores it in the
    /// routine's <see cref="FlowRegion.Total"/>.
    /// </summary>
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
    /// Returns the routines that control ever returns from. An exit from a routine is a reached
    /// block with no successor inside it. It may be a return, a tail jump, a <c>.fallthrough</c>
    /// into another routine, or a call to a routine that never returns. A routine returns when any
    /// of its exits does. A return always does, and a call that never returns does not. A tail jump
    /// or a fall-through does only when the routine it goes on into returns.
    /// <para>
    /// A routine is assumed not to return until something shows that it does. The set is
    /// recomputed until it stops changing, so a chain of tail jumps that ends in an infinite loop
    /// is found no matter how long the chain is. A call nt65 cannot identify, and a call to a
    /// routine with no body, are assumed to return. Saying they do not would be a claim about code
    /// that is not in the program. The exception is a routine declared <c>noreturn</c>, where the
    /// program makes that claim itself.
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

    /// <summary>Returns whether any exit from a routine is one through which control returns.</summary>
    private static bool ComesBack(
        FlowRegion region,
        Dictionary<(string Path, string Name), FlowRegion> regions,
        HashSet<(string Path, string Name)> found)
    {
        foreach (var block in region.Blocks)
        {
            if (!block.IsReached || block.Successors.Any(edge => edge.Kind != EdgeKind.Call))
                continue;
            var handsOff = Onward(block).Any(callee => regions.ContainsKey(Named(callee))
                ? !found.Contains(Named(callee))
                : callee.Signature is { NeverReturns: true });
            if (!handsOff)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a key that identifies a routine by its file and its flattened name, rather than by
    /// its symbol. An analysis that kept a file's results from before an edit holds a different
    /// symbol object for the same routine. The key uses the name and not the position, because a
    /// kept file still refers to the routines of an edited file at the positions they had before
    /// the edit.
    /// </summary>
    private static (string Path, string Name) Named(Symbol routine) => (routine.Tree.Path, routine.FlatName);

    /// <summary>
    /// Returns what <paramref name="region"/>'s routine costs with its calls, working out what
    /// each callee costs first. <paramref name="walking"/> holds the routines being worked out, so
    /// that a routine reaching itself is detected rather than followed round forever.
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
        walking.Add(name);

        // The lower and upper bounds are worked out separately, because a callee that loops has a
        // lower bound but no upper bound, and then so does its caller. What is left out is
        // gathered while working out the lower bound, which weighs every block a path reaches.
        // An upper bound that leaves anything out would not be one, so there is then no upper
        // bound.
        var excluded = new List<Exclusion>();
        var (least, _, ends) = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, true));
        var most = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, false)).Most;
        walking.Remove(name);
        var cost = new RoutineCost(
            least, excluded.Count > 0 ? null : most, Calls(region), ends && returns.Contains(name),
            region.Cost.Uncounted, excluded);
        totals[name] = cost;
        return cost;

        CycleCount? Weighed(BasicBlock block, bool fewest)
        {
            if (Paths.Costing(block) is not { } own)
                return null;
            var with = fewest ? own.Least : own.Most;

            // Where the call goes is not known, so all it costs is the call instruction itself.
            if (block.CallsUnknown)
            {
                if (!fewest)
                    return null;
                Exclude(new Exclusion(
                    block.Steps[^1].Statement.GetText().Trim(), "nt65 cannot tell where it goes"));
            }

            // A call inside a counted loop is made once per iteration, so its cost is counted
            // as many times as the loop runs.
            foreach (var callee in Onward(block))
            {
                var name = Named(callee);
                if (!regions.TryGetValue(name, out var called))
                {
                    // A callee declared never to return ends the pass like any other.
                    if (callee.Signature is { NeverReturns: true })
                        continue;
                    if (!fewest)
                        return null;
                    Exclude(new Exclusion(callee.QualifiedName, "no code in the program"));
                    continue;
                }

                // Control does not come back from a routine that never returns, so the pass
                // ends where it is called and what it does is no part of this one.
                if (!returns.Contains(name))
                    continue;

                // A routine that can reach itself goes round as many times as the program
                // decides, so each time round is left out.
                if (walking.Contains(name))
                {
                    if (!fewest)
                        return null;
                    Exclude(new Exclusion("recursion", $"{callee.QualifiedName} can call itself"));
                    continue;
                }
                var total = Total(called, regions, returns, totals, walking);
                if (!fewest)
                {
                    if (total.Most is not { } longest)
                        return null;
                    with += longest * (block.Turns ?? 1);
                    continue;
                }

                // A callee with no count of its own is left out whole. A callee whose calls leave
                // something out still counts the rest, and this routine leaves out the same items.
                if (total.Least is not { } shortest)
                {
                    Exclude(new Exclusion(callee.QualifiedName, called.Cost.Uncounted ?? "it has no count"));
                    continue;
                }
                with += shortest * (block.Turns ?? 1);
                foreach (var exclusion in total.Excluded ?? [])
                    Exclude(exclusion);
            }
            return new CycleCount(with);
        }

        // The lower-bound walk weighs a block more than once, so each item is kept only the first time.
        void Exclude(Exclusion exclusion)
        {
            if (!excluded.Any(known => known.What == exclusion.What))
                excluded.Add(exclusion);
        }
    }

    /// <summary>
    /// Returns whether a routine calls at all, which is what makes a total differ from its own
    /// cost.
    /// </summary>
    private static bool Calls(FlowRegion region) =>
        region.Blocks.Any(block => Onward(block).Any() || block.CallsUnknown);

    /// <summary>
    /// Returns the routines a block hands control to, whose cost is then part of this routine's.
    /// They are the routines it calls or tail-jumps to, and the routine its <c>.fallthrough</c>
    /// runs on into.
    /// </summary>
    private static IEnumerable<Symbol> Onward(BasicBlock block) =>
        block.RunsInto is { } into ? block.Calls.Append(into) : block.Calls;
}
