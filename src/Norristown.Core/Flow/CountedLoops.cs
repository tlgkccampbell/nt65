using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// The loops whose iteration count nt65 can work out. The program does not say how long most
/// loops run, but a counted loop (a register loaded with an immediate, decremented once per
/// iteration and branched on) states it outright, and such a loop is typically where a
/// routine's cycles go.
/// <para>
/// Only one shape is recognised: each iteration ends with a <c>dex</c> or <c>dey</c> and then
/// a <c>bne</c> or <c>bpl</c> back to the top, nothing else in the loop writes that register,
/// there is one way in, which loads the immediate, and one way out, which is the branch
/// itself. Every other loop is left uncounted, with no upper bound, because a loop counted
/// wrongly is worse than one not counted at all.
/// </para>
/// </summary>
internal static class CountedLoops
{
    /// <summary>
    /// Finds every counted loop in <paramref name="blocks"/> and writes what it costs on it.
    /// A loop inside another is settled first, so that the cost of one iteration of the
    /// enclosing loop already includes what the inner loop costs.
    /// </summary>
    public static void Find(SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks)
    {
        foreach (var loop in Loops.In(blocks))
        {
            if (Turns(model, blocks, loop) is not { } turns)
                continue;

            // The loop is marked as counted while the cost of an iteration is worked out, so
            // that the walk of it stops at the latch instead of going round again; if that
            // cost turns out to be unknown, the marking is undone.
            Mark(blocks, loop, turns);
            if (Repeated(layout, blocks, loop, turns) is { } cost)
                Settle(blocks, loop, cost);
            else
                Mark(blocks, loop, null);
        }
    }

    /// <summary>
    /// How many iterations a loop runs, or null when it is not a shape nt65 recognises. Every
    /// part of the shape has to hold: anything unexpected leaves the loop uncounted.
    /// </summary>
    private static int? Turns(SemanticModel model, IReadOnlyList<BasicBlock> blocks, Loop loop)
    {
        // Each iteration ends with the decrement immediately followed by the branch back:
        // anything between the two could set the flags the branch tests instead.
        var steps = Written(blocks[loop.Latch]);
        if (steps.Count < 2)
            return null;
        if (Mnemonic(steps[^1]) is not ("bne" or "bpl") || Mnemonic(steps[^2]) is not ("dex" or "dey"))
            return null;
        var counter = Mnemonic(steps[^2])!;
        var register = counter == "dex" ? Registers.X : Registers.Y;

        // The count may come down by more than one per iteration, as it does when the loop
        // walks an array of words: every decrement in the unbroken run just before the branch
        // executes on each iteration, so the stride is how many of them there are.
        var stride = 0;
        var counting = new HashSet<int>();
        for (var at = steps.Count - 2; at >= 0 && Mnemonic(steps[at]) == counter; at--)
        {
            stride++;
            counting.Add(steps[at].Statement.Position);
        }

        // There must be one way out, and it must be that branch. A loop that something else
        // can exit does not necessarily run as many times as the count says.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            var leaves = blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && !loop.Inside[edge.To]);
            if (leaves && i != loop.Latch)
                return null;
        }

        // Nothing else in it may write the register, or its value would no longer follow
        // from the immediate the loop started from.
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

        // `bne` runs the count down to zero, so the stride has to divide it or the count skips
        // past zero and wraps round. `bpl` runs one more iteration after zero and tests the sign
        // bit to stop, so a start with the sign bit set does not count down at all. What a start
        // of zero does depends on the register's width, which is not always known.
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
    /// Marks the loop as counted with <paramref name="turns"/> iterations, or unmarks it when
    /// that is null. Every block of it runs the same number of iterations, and a loop inside
    /// another runs all of its iterations on each iteration of the outer one, so the counts
    /// multiply. The back edge is marked so that a walk of the routine stops at the latch
    /// rather than going round again.
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
    /// What all the iterations of a loop cost, from what one iteration costs. The branch is
    /// taken on every iteration but the last, which is the only difference between the final
    /// iteration and the others.
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
    /// Records the loop's total cost on its header and zero on its other blocks: a walk of the
    /// routine still passes through all of them, and the header's total already includes every
    /// iteration of every block.
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
