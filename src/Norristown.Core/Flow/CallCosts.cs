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
    /// routine's <see cref="FlowRegion.Total"/>. The regions are changed in place, so a flow kept
    /// from an earlier analysis has to be the copy <see cref="ControlFlow.ForComposing"/> makes.
    /// </summary>
    internal static void Compose(IEnumerable<ControlFlow> flows)
    {
        var regions = new Dictionary<RoutineKey, FlowRegion>();
        foreach (var flow in flows)
        {
            foreach (var region in flow.Regions)
                regions.TryAdd(RoutineKey.Of(region.Routine), region);
        }
        var returns = Returning(regions);
        var totals = new Dictionary<RoutineKey, RoutineCost>();
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
    private static HashSet<RoutineKey> Returning(Dictionary<RoutineKey, FlowRegion> regions)
    {
        var found = new HashSet<RoutineKey>();
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

    /// <summary>
    /// Returns whether <paramref name="callee"/> is declared never to return. That is so where it
    /// declares <c>noreturn</c>, or where it is a label inside a routine that does. A label is
    /// judged only by its routine's declaration, as the flow graph judges it.
    /// </summary>
    private static bool DeclaredNeverReturns(Symbol callee) =>
        callee.Signature is { NeverReturns: true }
        || callee is { Kind: SymbolKind.Label, Signature: null, Routine.Signature.NeverReturns: true };

    /// <summary>Returns whether any exit from a routine is one through which control returns.</summary>
    private static bool ComesBack(
        FlowRegion region,
        Dictionary<RoutineKey, FlowRegion> regions,
        HashSet<RoutineKey> found)
    {
        foreach (var block in region.Blocks)
        {
            if (!block.IsReached || block.Successors.Any(edge => edge.Kind != EdgeKind.Call))
                continue;
            // The routines an exit hands control to are alternatives, so control comes back
            // through it when any one of them returns.
            var onward = Onward(block).ToList();
            var handsOff = onward.Count > 0 && onward.All(callee => regions.ContainsKey(RoutineKey.Of(callee))
                ? !found.Contains(RoutineKey.Of(callee))
                : DeclaredNeverReturns(callee));
            if (!handsOff)
                return true;
        }
        return false;
    }


    /// <summary>
    /// Returns what <paramref name="region"/>'s routine costs with its calls, working out what
    /// each callee costs first. <paramref name="walking"/> holds the routines being worked out, so
    /// that a routine reaching itself is detected rather than followed round forever.
    /// </summary>
    private static RoutineCost Total(
        FlowRegion region,
        Dictionary<RoutineKey, FlowRegion> regions,
        HashSet<RoutineKey> returns,
        Dictionary<RoutineKey, RoutineCost> totals,
        HashSet<RoutineKey> walking)
    {
        var name = RoutineKey.Of(region.Routine);
        if (totals.TryGetValue(name, out var found))
            return found;
        walking.Add(name);

        // The lower and upper bounds are worked out separately, because a callee that loops has a
        // lower bound but no upper bound, and then so does its caller. What is left out is
        // gathered while working out the lower bound, which weighs every block a path reaches.
        // An upper bound that leaves anything out would not be one, so there is then no upper
        // bound.
        var excluded = new List<Exclusion>();
        var (lowerBound, _, ends) = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, true));
        var upperBound = Paths.Through(region.Blocks, 0, _ => true, block => Weighed(block, false)).Maximum;
        walking.Remove(name);
        var cost = new RoutineCost(
            lowerBound, excluded.Count > 0 ? null : upperBound, Calls(region), ends && returns.Contains(name),
            region.Cost.Uncounted, excluded);
        totals[name] = cost;
        return cost;

        CycleCount? Weighed(BasicBlock block, bool forLowerBound)
        {
            if (Paths.Costing(block) is not { } own)
                return null;
            var with = forLowerBound ? own.Minimum : own.Maximum;

            // Where the call goes is not known, so all it costs is the call instruction itself.
            if (block.CallsUnknown)
            {
                if (!forLowerBound)
                    return null;
                Exclude(new Exclusion(
                    block.Steps[^1].Statement.GetText().Trim(), "nt65 cannot tell where it goes"));
            }

            // The routines a block hands control to are alternatives, of which one pass runs only
            // one, so the block adds the cheapest of them to the lower bound and the dearest to
            // the upper. A call inside a counted loop is made once per iteration, so its cost is
            // counted as many times as the loop runs.
            int? added = block.CallsUnknown ? 0 : null;
            foreach (var callee in Onward(block))
            {
                if (Callee(callee, forLowerBound) is not { } cost)
                    return null;
                added = added is not { } known ? cost : forLowerBound ? Math.Min(known, cost) : Math.Max(known, cost);
            }
            with += (added ?? 0) * (block.Iterations ?? 1);
            return new CycleCount(with);
        }

        // Returns what one routine a block hands control to adds to the bound, or null where the
        // upper bound has no value. What the lower bound cannot count adds nothing and is left out.
        int? Callee(Symbol callee, bool forLowerBound)
        {
            var name = RoutineKey.Of(callee);
            if (!regions.TryGetValue(name, out var called))
            {
                // A callee declared never to return ends the pass like any other, and so does a
                // label inside a routine declared so.
                if (DeclaredNeverReturns(callee))
                    return 0;
                if (!forLowerBound)
                    return null;
                Exclude(new Exclusion(callee.QualifiedName, "no code in the program"));
                return 0;
            }

            // Control does not come back from a routine that never returns, so the pass ends
            // where it is called and what it does is no part of this one.
            if (!returns.Contains(name))
                return 0;

            // A routine that can reach itself goes round as many times as the program decides,
            // so each time round is left out.
            if (walking.Contains(name))
            {
                if (!forLowerBound)
                    return null;
                Exclude(new Exclusion("recursion", $"{callee.QualifiedName} can call itself"));
                return 0;
            }
            var total = Total(called, regions, returns, totals, walking);
            if (!forLowerBound)
                return total.Maximum;

            // A callee with no count of its own is left out whole. A callee whose calls leave
            // something out still counts the rest, and this routine leaves out the same items.
            if (total.Minimum is not { } shortest)
            {
                Exclude(new Exclusion(callee.QualifiedName, called.Cost.Uncounted ?? "it has no count"));
                return 0;
            }
            foreach (var exclusion in total.Excluded ?? [])
                Exclude(exclusion);
            return shortest;
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
    /// They are the routines it calls or tail-jumps to, the places outside the routine that a
    /// <c>.next</c> under a jump names, and the routine its <c>.fallthrough</c> runs on into. Where
    /// there are several, they are alternatives, and one pass runs only one of them.
    /// </summary>
    internal static IEnumerable<Symbol> Onward(BasicBlock block)
    {
        if (block.RunsInto is { } into)
            return block.Calls.Append(into);

        // A jump with a `.next` naming places outside the routine is a tail jump to each of them,
        // as a jump naming one in its operand is.
        if (block.End == BlockEnd.Declared)
            return block.Calls.Concat(block.Leaves);
        return block.Calls;
    }
}
