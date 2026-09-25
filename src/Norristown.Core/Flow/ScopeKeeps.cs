using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which registers each inline <c>.scope</c> block of a routine leaves unchanged,
/// measured from where the scope is entered.
/// </summary>
internal static class ScopeKeeps
{
    /// <summary>
    /// Returns which registers each inline <c>.scope</c> block of <paramref name="region"/>
    /// leaves unchanged, following the routine with <paramref name="walk"/>. This is the question
    /// asked of the routine, but measured from where the scope is entered rather than where the
    /// routine was. A scope that saves a register and restores it keeps it, even where the
    /// routine around it does not.
    /// </summary>
    public static IReadOnlyList<ScopeRegisters> Of(
        RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of)
    {
        var found = new List<ScopeRegisters>();
        foreach (var (opener, whole) in region.Inline)
        {
            if (Within(walk, region, whole, of) is { } kept)
                found.Add(new ScopeRegisters(opener, kept.Kept, kept.Complete));
        }
        return found;
    }

    /// <summary>
    /// Returns which registers the part of a routine inside <paramref name="whole"/> leaves
    /// unchanged. It returns null where there is no single pass through that part to ask
    /// about, such as when a basic block holds part of it and part of something else. The
    /// shapes accepted are the ones a scope's cost is worked out for. The scope either lies
    /// inside one basic block or is made up of whole basic blocks.
    /// </summary>
    private static RoutineRegisters? Within(
        RegisterWalk walk, FlowRegion region, TextSpan whole, Func<Symbol, RoutineRegisters> of)
    {
        var blocks = region.Blocks;
        if (ScopeShape.Of(blocks, whole) is not { } shape)
            return null;

        // The scope lies inside one block, which runs all of it. What it keeps is what its own
        // statements leave, with nothing branching in or out of the middle of them.
        if (shape.IsStraight)
            return Straight(walk, blocks[shape.Entry], whole, of);

        var inside = shape.Inside;
        var solver = walk.Solver(blocks, of, block => ControlFlow.Onward(blocks, block).Where(to => inside[to]));
        solver.Enter(shape.Entry, RegisterState.Entered);
        var reached = solver.Reached;

        var kept = Registers.All;
        var complete = true;
        var leaves = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!inside[i] || reached[i] is not { } state)
                continue;
            complete &= RegisterWalk.Followed(blocks[i], of);

            // A block is an exit from the scope when what runs after it is outside the
            // scope. A return, or a jump to another routine, is an exit too.
            var left = walk.Leaves(blocks[i], region.Routine).ToList();
            if (left.Count == 0 && ControlFlow.Onward(blocks, blocks[i]).All(to => inside[to])
                && blocks[i].End != BlockEnd.Return)
            {
                continue;
            }
            leaves = true;
            var after = walk.Through(blocks[i], state, of, null);
            foreach (var into in left)
                after = RegisterWalk.Handed(after, of(into));
            kept &= after.Kept;
        }
        return leaves ? new RoutineRegisters(kept, complete) : null;
    }

    /// <summary>
    /// Returns which registers the statements of one block that lie inside a span leave
    /// unchanged.
    /// </summary>
    private static RoutineRegisters? Straight(
        RegisterWalk walk, BasicBlock block, TextSpan whole, Func<Symbol, RoutineRegisters> of)
    {
        var state = RegisterState.Entered;
        var within = false;
        var any = false;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            if (step.On is null)
                within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
            if (!within)
                continue;
            any = true;
            state = walk.Step(step, state, null, next: RegisterWalk.NextOf(block, i));
            if (i == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block))
                state = RegisterWalk.Calls(block, state, of);
        }
        return any ? new RoutineRegisters(state.Kept, RegisterWalk.Followed(block, of)) : null;
    }
}
