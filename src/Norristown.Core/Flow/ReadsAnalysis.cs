using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Works out which registers one routine uses the entry values of, which are the values its
/// caller has to give it. <see cref="RegisterKeeps"/> runs it over every routine of the program,
/// once what each routine keeps is settled, until the answers stop changing.
/// </summary>
internal static class ReadsAnalysis
{
    /// <summary>
    /// Returns which registers <paramref name="region"/>'s routine uses the entry values of,
    /// following it with <paramref name="walk"/>. <paramref name="of"/> gives what each routine
    /// it passes control to keeps, which must be settled, and <paramref name="reads"/> what each
    /// reads. <paramref name="readers"/> are the routines that reach below their own entry on the
    /// stack, which use what their caller pushed. <paramref name="sites"/>, where it is given,
    /// collects the first statement found to use each register's entry value, and the routine it
    /// was used through, if any. <paramref name="solved"/>, where it is given, is what reaches
    /// each block from <paramref name="start"/>, in place of what the walk finds from there.
    /// <paramref name="unfollowed"/>, where it is given, collects the first place where code nt65
    /// cannot follow may see each register's entry value, and the routine that code is, if any.
    /// </summary>
    public static RoutineReads Of(
        RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of, Func<Symbol, RoutineReads> reads,
        IReadOnlySet<RoutineKey> readers, Dictionary<Registers, (Step Step, Symbol? Through)>? sites = null,
        int start = 0, RegisterState?[]? solved = null,
        Dictionary<Registers, (Step Step, Symbol? Through)>? unfollowed = null)
    {
        var blocks = region.Blocks;
        if (!region.IsEntered || blocks.Count == 0)
            return RoutineReads.Nothing;
        var reached = solved ?? walk.Solved(region, of, start);

        var read = Registers.None;
        var complete = true;
        var bounded = false;
        var left = new RegisterState?[blocks.Count];
        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } entered)
                continue;
            var state = walk.Through(
                block, entered, of, null,
                (step, entries) => Use(entries, step, null),
                before => Called(block, before, block.End == BlockEnd.TailCall));

            // Returning with something still pushed returns through it, and a routine this one
            // passes control to may pull it.
            foreach (var into in walk.Leaves(block, region.Routine))
            {
                Given(reads(into), state, block, into);
                Use(state.Stack?.Entries ?? Registers.None, block.Steps[^1], into);
            }
            if (block.End == BlockEnd.Return)
                Use(state.Stack?.Entries ?? Registers.None, block.Steps[^1], null);
            left[block.Index] = state;
        }

        // Where two paths that pushed different things meet, nothing is known about the stack
        // after it, and a later pull may take any of those pushes back.
        foreach (var block in blocks)
        {
            if (left[block.Index] is not { Stack: { } stack } || block.Steps.Count == 0)
                continue;
            if (ControlFlow.Onward(blocks, block).Any(to => reached[to] is { Stack: null }))
                Use(stack.Entries, block.Steps[^1], null);
        }
        return new RoutineReads(read, complete, bounded);

        void Use(Registers entries, Step at, Symbol? through)
        {
            read |= entries;
            if (sites is null)
                return;
            foreach (var register in RegisterEffects.Each(entries))
                sites.TryAdd(register, (at, through));
        }

        // Adds what a routine uses of the values the registers hold where control passes to it.
        // One that may read anything leaves the answer incomplete only where something there
        // still holds one of this routine's entry values.
        void Given(RoutineReads callee, RegisterState state, BasicBlock block, Symbol through)
        {
            if (!callee.Complete)
                Unfollowed(state, block, through);
            bounded |= callee.Bounded;
            foreach (var register in RegisterEffects.Each(callee.Read))
                Use(state.Whole(register).Entry, block.Steps[^1], through);
        }

        // Adds what the routines a block ends by calling use. A call nt65 cannot follow may use
        // anything, which only an incomplete answer can say, as for a routine whose body is
        // not in the program. A routine that takes arguments, or that reaches below its own
        // entry on the stack, uses what is pushed, and so may the routine a tail call hands the
        // stack to.
        void Called(BasicBlock block, RegisterState state, bool tail)
        {
            var pushed = state.Stack?.Entries ?? Registers.None;
            if (block.CallsUnknown || block.Calls.Count == 0)
            {
                Unfollowed(state, block, null);
                return;
            }
            foreach (var callee in block.Calls)
            {
                Given(reads(callee), state, block, callee);
                if (tail || callee.Signature is { Pushed: > 0 } || readers.Contains(RoutineKey.Of(callee)))
                    Use(pushed, block.Steps[^1], callee);
            }
        }

        // Notes that control passes to code nt65 cannot follow at the end of a block. That leaves
        // the answer incomplete only where the code may see one of this routine's entry values.
        // Otherwise the answer is complete for the registers the analysis tracks, and only those.
        void Unfollowed(RegisterState state, BasicBlock block, Symbol? through)
        {
            var seen = Visible(state);
            if (seen == Registers.None)
            {
                bounded = true;
                return;
            }
            complete = false;
            if (unfollowed is null)
                return;
            foreach (var register in RegisterEffects.Each(seen))
                unfollowed.TryAdd(register, (block.Steps[^1], through));
        }
    }

    /// <summary>
    /// Returns the registers whose entry values code nt65 cannot follow may read at a point.
    /// Such code sees only the registers and the stack, so where every register holds something
    /// else and nothing pushed holds an entry value, it cannot read any of them. A stack whose
    /// contents are not known may hold anything.
    /// </summary>
    private static Registers Visible(RegisterState state)
    {
        if (state.Stack is not { } stack)
            return Registers.All;
        var seen = stack.Entries;
        foreach (var register in RegisterEffects.Each(Registers.All))
            seen |= state.Whole(register).Entry;
        return seen;
    }
}
