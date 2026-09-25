using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the loops whose iteration count nt65 can work out. The program does not say how long
/// most loops run, but a counted loop states it outright. In a counted loop a register is loaded
/// with an immediate, decremented once per iteration and branched on. Such a loop is typically
/// where a routine's cycles go.
/// <para>
/// Only one shape is recognised. Each iteration ends with a <c>dex</c> or <c>dey</c> and then a
/// <c>bne</c> or <c>bpl</c> back to the top. Nothing else in the loop, and no routine it calls,
/// writes that register.
/// There is one way in, which loads the immediate, and one way out, which is the branch itself.
/// Every other loop is left uncounted, with no upper bound, because a loop counted wrongly is
/// worse than one not counted at all.
/// </para>
/// </summary>
internal static class CountedLoops
{
    /// <summary>
    /// Finds every counted loop in <paramref name="blocks"/> and records its cost on its blocks.
    /// A loop inside another is handled first, so that the cost of one iteration of the
    /// enclosing loop already includes what the inner loop costs. <paramref name="bodies"/> holds
    /// the blocks of every routine in the file, which shows what a routine the loop calls writes.
    /// </summary>
    public static void Find(
        SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks,
        IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies)
    {
        foreach (var loop in Loops.In(blocks))
        {
            if (IterationCount(model, layout, blocks, loop, bodies) is not { } iterations)
                continue;

            // The loop is marked as counted while the cost of an iteration is worked out, so
            // that the walk of it stops at the latch instead of going round again. If that
            // cost turns out to be unknown, the marking is undone.
            Mark(blocks, loop, iterations);
            if (Repeated(layout, blocks, loop, iterations) is { } cost)
                RecordCost(blocks, loop, cost);
            else
                Mark(blocks, loop, null);
        }
    }

    /// <summary>
    /// Returns how many iterations a loop runs, or null when it is not a shape nt65 recognises.
    /// Every part of the shape has to hold, and anything unexpected leaves the loop uncounted.
    /// </summary>
    private static int? IterationCount(
        SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks, Loop loop,
        IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies)
    {
        // Each iteration ends with the decrement immediately followed by the branch back,
        // because anything between the two could set the flags the branch tests instead.
        var steps = InstructionsIn(blocks[loop.Latch]);
        if (steps.Count < 2)
            return null;
        if (Mnemonic(steps[^1]) is not (MnemonicKind.Bne or MnemonicKind.Bpl)
            || Mnemonic(steps[^2]) is not (MnemonicKind.Dex or MnemonicKind.Dey))
        {
            return null;
        }
        var counter = Mnemonic(steps[^2]);
        var register = counter == MnemonicKind.Dex ? Registers.X : Registers.Y;

        // The count may come down by more than one per iteration, as it does when the loop
        // walks an array of words. Every decrement in the unbroken run just before the branch
        // executes on each iteration, so the stride is how many of them there are.
        var stride = 0;
        var counting = new HashSet<StepKey>();
        for (var at = steps.Count - 2; at >= 0 && Mnemonic(steps[at]) == counter; at--)
        {
            stride++;
            counting.Add(steps[at].Key);
        }

        // There must be one way out, and it must be that branch. A loop that something else
        // can exit does not necessarily run as many times as the count says.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            var leaves = blocks[i].BranchesOut
                || blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && !loop.Inside[edge.To]);
            if (leaves && i != loop.Latch)
                return null;
        }

        // Nothing else in it may write the register, or its value would no longer follow
        // from the immediate the loop started from. That includes a routine the loop calls.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            if (MayWrite(blocks[i], loop, register, bodies))
                return null;
            foreach (var step in InstructionsIn(blocks[i]))
            {
                if (i == loop.Latch && counting.Contains(step.Key))
                    continue;
                if (Writes(Mnemonic(step), register))
                    return null;
            }
        }

        // There must be one way in, and it must load the immediate the count starts at.
        var from = new List<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i] && blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && edge.To == loop.Header))
                from.Add(i);
        }
        if (from.Count != 1 || Started(model, layout, blocks[from[0]], counter == MnemonicKind.Dex ? MnemonicKind.Ldx : MnemonicKind.Ldy, register) is not { } start)
            return null;

        // `bne` runs the count down to zero, so the stride has to divide it or the count skips
        // past zero and wraps round. `bpl` runs one more iteration after zero and tests the sign
        // bit to stop, so a start with the sign bit set does not count down at all. What a start
        // of zero does depends on the register's width, which is not always known.
        if (start <= 0)
            return null;
        long iterations;
        if (Mnemonic(steps[^1]) == MnemonicKind.Bne)
        {
            if (start % stride != 0)
                return null;
            iterations = start / stride;
        }
        else
        {
            if (start >= 0x80)
                return null;
            iterations = (start / stride) + 1;
        }
        return iterations is > 0 and <= 0x10000 ? (int)iterations : null;
    }

    /// <summary>
    /// Returns the immediate the block before the loop leaves in the register, or null when it
    /// leaves anything else there. The last value the block writes to the register is what the
    /// loop starts from.
    /// </summary>
    private static long? Started(SemanticModel model, CodeLayout layout, BasicBlock before, MnemonicKind load, Registers register)
    {
        long? started = null;
        foreach (var step in InstructionsIn(before))
        {
            var mnemonic = Mnemonic(step);
            if (!Writes(mnemonic, register))
                continue;
            started = mnemonic == load ? StepOperands.Immediate(model, layout, step) : null;
        }
        return started;
    }

    /// <summary>
    /// Marks the loop as counted with <paramref name="iterations"/> iterations, or unmarks it when
    /// that is null. Every block of it runs the same number of iterations, and a loop inside
    /// another runs all of its iterations on each iteration of the outer one, so the counts
    /// multiply. The back edge is marked so that a walk of the routine stops at the latch
    /// rather than going round again.
    /// </summary>
    private static void Mark(IReadOnlyList<BasicBlock> blocks, Loop loop, int? iterations)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            blocks[i].Iterations = iterations is { } many ? (blocks[i].Iterations ?? 1) * many : null;
            blocks[i].Repeats = iterations is null ? null : i == loop.Latch ? loop.Header : blocks[i].Repeats;
        }
    }

    /// <summary>
    /// Returns what all the iterations of a loop cost, from what one iteration costs. The branch is
    /// taken on every iteration but the last, which is the only difference between the final
    /// iteration and the others.
    /// </summary>
    private static CycleCount? Repeated(CodeLayout layout, IReadOnlyList<BasicBlock> blocks, Loop loop, int iterations)
    {
        var (minimum, maximum, _) = Paths.Through(blocks, loop.Header, at => loop.Inside[at], Paths.Costing);
        if (minimum is not { } low || maximum is not { } high)
            return null;
        var last = blocks[loop.Latch].Steps.LastOrDefault(step => !step.IsMarker);
        if (layout.Of(last.Statement, last.On)?.Cycles is not { } branch)
            return null;

        // A branch costs its fewest cycles when it is not taken, and one more than that when it
        // is taken. A taken branch that crosses a page costs its most cycles.
        var taken = new CycleCount(branch.Minimum + 1, branch.Maximum);
        return new CycleCount(
            ((low - branch.Minimum) * iterations) + (taken.Minimum * (iterations - 1)) + branch.Minimum,
            ((high - branch.Maximum) * iterations) + (taken.Maximum * (iterations - 1)) + branch.Minimum);
    }

    /// <summary>
    /// Records the loop's total cost on its header and zero on its other blocks. A walk of the
    /// routine still passes through all of them, and the header's total already includes every
    /// iteration of every block.
    /// </summary>
    private static void RecordCost(IReadOnlyList<BasicBlock> blocks, Loop loop, CycleCount cost)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (loop.Inside[i])
                blocks[i].LoopCycles = i == loop.Header ? cost : new CycleCount(0);
        }
    }

    /// <summary>
    /// Returns whether a call that <paramref name="block"/> makes may leave
    /// <paramref name="register"/> holding something else. A call to a place nt65 cannot name,
    /// or to a label of this routine outside the loop, may write anything.
    /// </summary>
    private static bool MayWrite(
        BasicBlock block, Loop loop, Registers register, IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies) =>
        block.CallsUnknown
        || block.Calls.Any(callee => !LeftAlone(callee, bodies, []).HasFlag(register))
        || block.Successors.Any(edge => edge.Kind == EdgeKind.Call && !loop.Inside[edge.To]);

    /// <summary>
    /// Returns the index registers that a call to <paramref name="callee"/> surely leaves as they
    /// were. Which registers a routine keeps is worked out only once the whole program has been
    /// analyzed, which is after loops are counted. So a routine is trusted only where its
    /// signature declares that it keeps a register, or where it has a body in this file that
    /// nothing in, and nothing it calls, writes that register.
    /// </summary>
    /// <param name="callee">The routine called.</param>
    /// <param name="bodies">The blocks of every routine in the file.</param>
    /// <param name="visiting">
    /// The routines whose bodies are being read further up. A routine that reaches itself is
    /// trusted only as far as its signature says.
    /// </param>
    private static Registers LeftAlone(
        Symbol callee, IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies, HashSet<Symbol> visiting)
    {
        var declared = callee.Signature?.Keeps ?? Registers.None;
        if (!bodies.TryGetValue(callee, out var body) || !visiting.Add(callee))
            return declared;
        var alone = Registers.X | Registers.Y;
        foreach (var block in body)
        {
            if (block.CallsUnknown || block.Successors.Any(edge => edge.Kind == EdgeKind.Call))
                alone = Registers.None;
            foreach (var called in block.Calls)
                alone &= LeftAlone(called, bodies, visiting);
            if (block.RunsInto is { } into)
                alone &= LeftAlone(into, bodies, visiting);
            foreach (var step in InstructionsIn(block))
                alone &= ~WrittenBy(Mnemonic(step));
        }
        visiting.Remove(callee);
        return declared | alone;
    }

    /// <summary>
    /// Returns the index registers an instruction may change. Changing the index width on the
    /// 65816 clears their high bytes, so an instruction that may do that counts as writing both.
    /// </summary>
    private static Registers WrittenBy(MnemonicKind mnemonic) =>
        mnemonic is MnemonicKind.Rep or MnemonicKind.Sep or MnemonicKind.Plp or MnemonicKind.Xce or MnemonicKind.Rti
            ? Registers.X | Registers.Y
            : Instructions.Facts(mnemonic).Writes & (Registers.X | Registers.Y);

    /// <summary>Returns whether a mnemonic may leave <paramref name="register"/> holding something else.</summary>
    private static bool Writes(MnemonicKind mnemonic, Registers register) =>
        Instructions.Facts(mnemonic).Writes.HasFlag(register);

    /// <summary>Returns the instructions in a block, leaving out markers and directives.</summary>
    private static List<Step> InstructionsIn(BasicBlock block) =>
        [.. block.Steps.Where(step => !step.IsMarker && step.Statement is InstructionStatementSyntax)];

    /// <summary>Returns the mnemonic of a step's statement, or <see cref="MnemonicKind.None"/>.</summary>
    private static MnemonicKind Mnemonic(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.MnemonicKind ?? MnemonicKind.None;
}
