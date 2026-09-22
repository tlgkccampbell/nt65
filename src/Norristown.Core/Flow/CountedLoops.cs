using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// The loops nt65 can say how many turns of. How long most loops run is not in the program,
/// but the counted loop — a register loaded with an immediate, brought down once a turn and
/// branched on — says it outright, and it is the loop the cycles go in.
/// <para>
/// Only one shape is read: the turn ends with a <c>dex</c> or a <c>dey</c> and a <c>bne</c> or
/// a <c>bpl</c> back to the top, nothing else in the loop touches that register, there is one
/// way in, which carries the immediate, and one way out, which is the branch itself. Every
/// other loop keeps the bound it had, because a loop counted wrongly is worse than one not
/// counted at all.
/// </para>
/// </summary>
internal static class CountedLoops
{
    /// <summary>
    /// Finds every counted loop in <paramref name="blocks"/> and writes what it costs on it.
    /// A loop inside another is settled first, so that the turn of the loop around it already
    /// holds what the inner one costs.
    /// </summary>
    public static void Find(SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks)
    {
        foreach (var loop in Loops.In(blocks))
        {
            if (Turns(model, blocks, loop) is not { } turns)
                continue;

            // The loop is taken as counted while its turn is worked out, so that the walk of
            // it stops at the latch instead of going round again; if the turn turns out not
            // to be known, it is given back.
            Mark(blocks, loop, turns);
            if (Repeated(layout, blocks, loop, turns) is { } cost)
                Settle(blocks, loop, cost);
            else
                Mark(blocks, loop, null);
        }
    }

    /// <summary>
    /// How many turns a loop takes, or null when it is not one nt65 reads. Every part of the
    /// shape has to hold: anything unexpected leaves the loop uncounted.
    /// </summary>
    private static int? Turns(SemanticModel model, IReadOnlyList<BasicBlock> blocks, Loop loop)
    {
        // The turn ends with the counter and the branch that takes it round again: anything
        // between the two would set the flags the branch reads instead.
        var steps = Written(blocks[loop.Latch]);
        if (steps.Count < 2)
            return null;
        if (Mnemonic(steps[^1]) is not ("bne" or "bpl") || Mnemonic(steps[^2]) is not ("dex" or "dey"))
            return null;
        var counter = Mnemonic(steps[^2])!;
        var register = counter == "dex" ? Registers.X : Registers.Y;

        // The count may come down by more than one a turn, as it does where it walks an array
        // of words: every one of them runs on the turn, being written in a row before the
        // branch, so the count comes down by as many as there are.
        var stride = 0;
        var counting = new HashSet<int>();
        for (var at = steps.Count - 2; at >= 0 && Mnemonic(steps[at]) == counter; at--)
        {
            stride++;
            counting.Add(steps[at].Statement.Position);
        }

        // One way out of it, and it is that branch. A loop something else breaks out of does
        // not take its turns however many times the count says.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            var leaves = blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && !loop.Inside[edge.To]);
            if (leaves && i != loop.Latch)
                return null;
        }

        // Nothing else in it may touch the register, or how far it has come is not the
        // immediate the loop started from.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            foreach (var step in Written(blocks[i]))
            {
                if (i == loop.Latch && counting.Contains(step.Statement.Position))
                    continue;
                if (Mnemonic(step) is { } other && Writes(other, register))
                    return null;
            }
        }

        // One way in, carrying the immediate the count starts at.
        var from = new List<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i] && blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && edge.To == loop.Header))
                from.Add(i);
        }
        if (from.Count != 1 || Started(model, blocks[from[0]], counter == "dex" ? "ldx" : "ldy", register) is not { } start)
            return null;

        // `bne` runs the count down to zero, so the stride has to divide it or the count goes
        // past zero and round. `bpl` runs one turn past zero, and reads the sign to know it, so
        // a count that starts with the sign bit set is not one it counts down from at all. A
        // count of zero turns on how wide the register is, which is not always known.
        if (start <= 0)
            return null;
        long turns;
        if (Mnemonic(steps[^1]) == "bne")
        {
            if (start % stride != 0)
                return null;
            turns = start / stride;
        }
        else
        {
            if (start >= 0x80)
                return null;
            turns = (start / stride) + 1;
        }
        return turns is > 0 and <= 0x10000 ? (int)turns : null;
    }

    /// <summary>
    /// The immediate the block before the loop leaves in the register, or null when what it
    /// leaves there is anything else. The last thing it writes is what the loop starts from.
    /// </summary>
    private static long? Started(SemanticModel model, BasicBlock before, string load, Registers register)
    {
        long? started = null;
        foreach (var step in Written(before))
        {
            if (Mnemonic(step) is not { } mnemonic || !Writes(mnemonic, register))
                continue;
            started = mnemonic == load ? Immediate(model, step) : null;
        }
        return started;
    }

    /// <summary>
    /// Takes the loop as counted, or gives it back. Every block of it runs the same number of
    /// turns, and a loop inside another runs its own turns on each of the turns around it, so
    /// the counts multiply. The back edge is marked so that a walk of the routine stops at the
    /// latch rather than coming round again.
    /// </summary>
    private static void Mark(IReadOnlyList<BasicBlock> blocks, Loop loop, int? turns)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            blocks[i].Turns = turns is { } many ? (blocks[i].Turns ?? 1) * many : null;
            blocks[i].Repeats = turns is null ? null : i == loop.Latch ? loop.Header : blocks[i].Repeats;
        }
    }

    /// <summary>
    /// What all the turns of a loop cost, from what one turn costs. The branch is taken every
    /// turn but the last, which is the one difference between a turn and the turn that leaves.
    /// </summary>
    private static CycleCount? Repeated(CodeLayout layout, IReadOnlyList<BasicBlock> blocks, Loop loop, int turns)
    {
        var (least, most, _) = Paths.Through(blocks, loop.Header, at => loop.Inside[at], Paths.Costing);
        if (least is not { } low || most is not { } high)
            return null;
        var last = blocks[loop.Latch].Steps.LastOrDefault(step => !step.IsMarker);
        if (layout.Of(last.Statement, last.On)?.Cycles is not { } branch)
            return null;

        // A branch costs its fewest when it is not taken, and one more than that when it is; a
        // taken one that crosses a page costs the most of all.
        var taken = new CycleCount(branch.Least + 1, branch.Most);
        return new CycleCount(
            ((low - branch.Least) * turns) + (taken.Least * (turns - 1)) + branch.Least,
            ((high - branch.Most) * turns) + (taken.Most * (turns - 1)) + branch.Least);
    }

    /// <summary>
    /// Writes what the loop costs on its header, and nothing on the rest of it: a walk of the
    /// routine runs through all of them, and every turn of every one is already in that.
    /// </summary>
    private static void Settle(IReadOnlyList<BasicBlock> blocks, Loop loop, CycleCount cost)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (loop.Inside[i])
                blocks[i].LoopCycles = i == loop.Header ? cost : new CycleCount(0);
        }
    }

    /// <summary>Whether a mnemonic may leave <paramref name="register"/> holding something else.</summary>
    private static bool Writes(string mnemonic, Registers register) =>
        Instructions.Facts(mnemonic).Writes.HasFlag(register);

    /// <summary>The instructions written in a block, the markers and the directives aside.</summary>
    private static List<Step> Written(BasicBlock block) =>
        [.. block.Steps.Where(step => !step.IsMarker && step.Statement is InstructionStatementSyntax)];

    /// <summary>The mnemonic a step's statement is written with, lower case, or null.</summary>
    private static string? Mnemonic(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.Mnemonic.Text.ToLowerInvariant();

    /// <summary>The value of the immediate a step is written with, or null when it has none nt65 knows.</summary>
    private static long? Immediate(SemanticModel model, Step step) =>
        step.Statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax operand }
            ? model.ValueOf(operand.Value, step.On).AsNumber()
            : null;
}
