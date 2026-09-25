using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Works out which registers one routine returns holding the values it was entered with, and
/// reports where a routine breaks its <c>keeps</c> promise. <see cref="RegisterKeeps"/> runs it
/// over every routine of the program until the answers stop changing.
/// </summary>
internal static class KeepsAnalysis
{
    /// <summary>
    /// Returns what <paramref name="region"/>'s routine keeps, following it with
    /// <paramref name="walk"/>, with <paramref name="of"/> giving what each routine it calls keeps. <paramref name="report"/> collects what is wrong with
    /// the routine on the final walk, once the answer has reached a fixed point. Earlier rounds
    /// pass null and report nothing. <paramref name="start"/> is the index of the block the
    /// routine is entered at, which is a label's where another routine calls or jumps to it.
    /// </summary>
    public static RoutineRegisters Of(
        RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report,
        int start = 0)
    {
        var blocks = region.Blocks;
        if (!region.IsEntered || blocks.Count == 0)
            return RoutineRegisters.Everything;

        // The promise is about calls to the routine, so it is checked on the paths from the
        // routine's entry. Code reached only from a declared label that another routine jumps
        // to is followed too, for what an editor shows and for its `.state` items, but what it
        // leaves is not held to the promise.
        var reached = walk.Solve(region, of, start, fromOutside: report is not null);
        var entered = report is null ? reached : walk.Solve(region, of, start, fromOutside: false);
        var kept = Registers.All;
        var complete = true;
        var leaves = false;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state)
                continue;
            var through = walk.Through(block, state, of, report);
            var after = ReferenceEquals(reached, entered) ? through
                : entered[block.Index] is { } fromEntry ? walk.Through(block, fromEntry, of, null)
                : null;
            if (after is null)
                continue;
            if (block.CallsUnknown)
                complete = false;
            foreach (var callee in block.Calls)
            {
                if (!of(callee).Complete)
                    complete = false;
            }

            // A path that passes control to another routine is an exit from this one, and is
            // checked against the registers that routine returns unchanged.
            var left = false;
            foreach (var into in walk.Leaves(block, region.Routine))
            {
                var handed = of(into);
                if (!handed.Complete)
                    complete = false;
                var onExit = RegisterWalk.Handed(after, handed);
                leaves = true;
                left = true;
                kept &= onExit.Kept;
                if (report is not null)
                    Check(region, block, onExit, into, handed.Kept, report);
            }
            // `stp` and `jam` stop the processor, so nothing ever reads what they left. `rti`
            // goes back to the code the interrupt broke into, which is exactly where the
            // registers matter.
            if (left || block.End is not (BlockEnd.Return or BlockEnd.TailCall))
                continue;
            leaves = true;
            kept &= after.Kept;
            if (report is not null)
                Check(region, block, after, null, Registers.None, report);
        }

        // A routine no path leaves never returns anything to a caller, so there is nothing
        // it can fail to keep. What it does to the registers matters to no one else.
        return leaves ? new RoutineRegisters(kept, complete) : RoutineRegisters.Everything;
    }

    /// <summary>
    /// Reports a diagnostic where a routine's <c>keeps</c> promise does not hold at a point a
    /// path leaves it, saying what to change. <paramref name="into"/> is the routine or
    /// label the path passes control to, if any, and <paramref name="kept"/> the registers
    /// that routine returns unchanged.
    /// </summary>
    private static void Check(
        FlowRegion region, BasicBlock block, RegisterState state, Symbol? into, Registers kept,
        List<Diagnostic> report)
    {
        if (region.Routine.Signature?.Keeps is not { } promised || promised == Registers.None)
            return;
        var broken = promised & ~state.Kept;
        if (broken == Registers.None)
            return;
        var at = block.Steps.Count > 0
            ? block.Steps[^1].Statement.Tree.GetSpan(block.Steps[^1].Statement.Span)
            : region.Routine.DeclarationSpan;
        var names = RegisterEffects.Format(broken);
        var items = names.ToLowerInvariant();
        var one = RegisterEffects.Each(broken).Count() == 1;

        // This routine cannot promise registers that the routine the path passes control to
        // makes no promise about. The promise belongs on the routine whose code has to
        // honour it.
        var missing = into is not null ? broken & ~kept : Registers.None;

        // When the stack is unknown, that is why the restore could not be seen. Saying what
        // made it unknown points nearer the mistake than telling the routine to restore the
        // register again.
        var fix = missing != Registers.None
            ? Handing(into!, missing)
            : state.Stack is null && state.WhyStack is { } lost
                ? Cause.Because(lost)
                : $": restore {(one ? "it" : "them")} before returning, or add `.state keeps {items}` "
                    + "at the point where the entry value is restored";
        report.Add(new Diagnostic(at,
            Catalogue.KeepsBroken.Message(
                region.Routine.DisplayName, items, names, one ? "is" : "are", fix)));
    }

    /// <summary>
    /// Returns the fix to suggest where handing control to another routine is what loses the
    /// registers. The promise goes on the routine handed to, since that is the code the
    /// register has to come back through. Control never comes back here to restore anything.
    /// Where control goes to a label inside a routine, the path from that label has to restore
    /// the registers, since a promise on the routine does not cover it.
    /// </summary>
    private static string Handing(Symbol into, Registers missing)
    {
        var owner = RegisterWalk.Owner(into)!;
        var name = owner.DisplayName;
        var items = RegisterEffects.Format(missing).ToLowerInvariant();
        var one = RegisterEffects.Each(missing).Count() == 1;

        // Where the path names a label rather than the routine itself, what is kept is what the
        // path from that label keeps, which no promise on the routine changes.
        if (owner != into)
        {
            return $": control does not come back from `{into.DisplayName}` in `{name}`, and the path from "
                + $"there does not keep {items}: restore {(one ? "it" : "them")} there, "
                + "or add `.next ?` here to end the path unchecked";
        }
        var gone = $"control does not come back from `{name}`, which does not promise to keep {items}";
        return $": {gone}: add `keeps {items}` to `{name}` if it preserves {(one ? "it" : "them")}, "
            + "or add `.next ?` here to end the path unchecked";
    }
}
