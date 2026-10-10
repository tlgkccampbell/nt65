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
/// The loop's test is a <c>dex</c> or <c>dey</c> immediately followed by a <c>bne</c> or
/// <c>bpl</c>. The test may end the loop and branch back to its top. It may instead begin the
/// loop, where a jump enters the loop at the test, the test branches into the body, and the body
/// runs back into the test. Nothing else in the loop, and no routine it calls, writes that
/// register, and neither does a routine called after the load. On the 65816 a change to the
/// index width counts as a write. There is one way in, which loads the immediate, and one way
/// out, which is the test's branch. Every other loop is left uncounted, with no upper bound,
/// because a loop counted wrongly is worse than one not counted at all.
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
            if (IterationCount(model, layout, blocks, loop, bodies) is not { } counted)
                continue;

            // The loop is marked as counted while the cost of an iteration is worked out, so
            // that the walk of it stops at the latch instead of going round again. If that
            // cost turns out to be unknown, the marking is undone, which leaves the marks of
            // any loop inside it as they were.
            var before = Mark(blocks, loop, counted);
            if (Repeated(layout, blocks, loop, counted) is { } cost)
                RecordCost(blocks, loop, cost);
            else
                Restore(blocks, before);
        }
    }

    /// <summary>
    /// Returns how many times a loop's test runs each time the loop is entered, and which block
    /// holds the test, or null when the loop is not a shape nt65 recognizes. Every part of the
    /// shape has to hold, and anything unexpected leaves the loop uncounted.
    /// </summary>
    private static Counted? IterationCount(
        SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks, Loop loop,
        IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies)
    {
        if (TestOf(blocks, loop) is not { } test)
            return null;
        var steps = InstructionsIn(blocks[test]);
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

        // There must be one way out, and it must be the test's branch. A loop that something
        // else can exit does not necessarily run as many times as the count says.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            var leaves = blocks[i].BranchesOut
                || blocks[i].Successors.Any(edge => edge.Kind != EdgeKind.Call && !loop.Inside[edge.To]);
            if (leaves && i != test)
                return null;
        }

        // Nothing else in it may write the register, or its value would no longer follow
        // from the immediate the loop started from. That includes a routine the loop calls.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            if (MayWrite(layout, blocks[i], loop, register, bodies) || HiddenWrites(layout, blocks[i]).HasFlag(register))
                return null;
            foreach (var step in InstructionsIn(blocks[i]))
            {
                if (i == test && counting.Contains(step.Key))
                    continue;
                if (WrittenBy(model, layout, step).HasFlag(register))
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
        var load = counter == MnemonicKind.Dex ? MnemonicKind.Ldx : MnemonicKind.Ldy;
        if (from.Count != 1 || Started(model, layout, blocks[from[0]], load, register, bodies) is not var (start, bits))
            return null;
        return Trips(start, bits, stride, Mnemonic(steps[^1])) is { } trips ? new Counted(trips, test) : null;
    }

    /// <summary>
    /// Returns the block whose decrement and branch decide whether a loop goes round again, or
    /// null where the loop has no such block. Where the loop tests at the bottom, the test is the
    /// latch, whose taken branch is the back edge. Where a jump enters the loop at its test, as
    /// <c>jmp test</c> does, the test is the header. Its branch is taken into the body, and the
    /// latch goes back to it unconditionally.
    /// </summary>
    private static int? TestOf(IReadOnlyList<BasicBlock> blocks, Loop loop)
    {
        var latch = blocks[loop.Latch];
        if (CountsDown(latch) && latch.Successors.Any(edge => edge.Kind == EdgeKind.Taken && edge.To == loop.Header))
            return loop.Latch;
        var header = blocks[loop.Header];
        if (loop.Header != loop.Latch && CountsDown(header) && header.LoopCycles is null
            && header.Successors.Any(edge => edge.Kind == EdgeKind.Taken && edge.To != loop.Header && loop.Inside[edge.To])
            && latch.End != BlockEnd.Branch
            && latch.Successors.Where(edge => edge.Kind != EdgeKind.Call).All(edge => edge.To == loop.Header))
        {
            return loop.Header;
        }
        return null;
    }

    /// <summary>
    /// Returns a value indicating whether a block ends with a <c>dex</c> or <c>dey</c> immediately
    /// followed by a <c>bne</c> or <c>bpl</c>. Anything between the two could set the flags the
    /// branch tests instead.
    /// </summary>
    private static bool CountsDown(BasicBlock block)
    {
        var steps = InstructionsIn(block);
        return steps.Count >= 2
            && Mnemonic(steps[^1]) is MnemonicKind.Bne or MnemonicKind.Bpl
            && Mnemonic(steps[^2]) is MnemonicKind.Dex or MnemonicKind.Dey;
    }

    /// <summary>
    /// Returns how many times a test runs before its branch stops going round, or null where that
    /// does not follow from the start.
    /// </summary>
    /// <param name="start">The immediate the register is loaded with.</param>
    /// <param name="bits">How wide the register is, or null where that is not known.</param>
    /// <param name="stride">How many decrements the test makes each time it runs.</param>
    /// <param name="branch">The <c>bne</c> or <c>bpl</c> that ends the test.</param>
    private static int? Trips(long start, int? bits, int stride, MnemonicKind branch)
    {
        if (start < 0)
            return null;
        long trips;
        if (branch == MnemonicKind.Bne)
        {
            // `bne` runs the count down to zero, so the stride has to divide it or the count skips
            // past zero and wraps round. A start of zero wraps round before the first test, so the
            // count is the register's whole range, which is 256 for an 8-bit register. Where the
            // width is not known, neither is that range.
            var total = start == 0 && bits is { } width ? 1L << width : start;
            if (total == 0 || total % stride != 0)
                return null;
            trips = total / stride;
        }
        else
        {
            // `bpl` runs one more iteration after zero and tests the sign bit to stop, so a start
            // with the sign bit set does not count down at all. Where the width is not known, the
            // 8-bit sign bit is used, since a start below it counts the same at either width.
            var sign = bits == 16 ? 0x8000 : 0x80;
            if (start >= sign)
                return null;
            trips = (start / stride) + 1;
        }
        return trips is > 0 and <= 0x10000 ? (int)trips : null;
    }

    /// <summary>
    /// Returns the immediate the block before the loop leaves in the register, and how wide the
    /// register is there, or null when the block leaves anything else there. The last value the
    /// block writes to the register is what the loop starts from. A call that ends the block
    /// counts as writing the register unless the routine it calls surely leaves it alone, as a
    /// call inside the loop does. A call to a label of this routine always counts.
    /// </summary>
    private static (long Start, int? Bits)? Started(
        SemanticModel model, CodeLayout layout, BasicBlock before, MnemonicKind load, Registers register,
        IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies)
    {
        (long, int?)? started = null;
        foreach (var step in InstructionsIn(before))
        {
            var mnemonic = Mnemonic(step);
            if (!WrittenBy(model, layout, step).HasFlag(register))
                continue;
            started = mnemonic == load && StepOperands.Immediate(model, layout, step) is { } value
                ? (value, WidthOf(layout, step))
                : null;
        }
        var calls = CallMayWrite(layout, before, register, bodies) || before.Successors.Any(edge => edge.Kind == EdgeKind.Call);
        return calls ? null : started;
    }

    /// <summary>
    /// Returns how wide the index register an immediate load at <paramref name="load"/> writes is,
    /// or null where that is not known. Only the 65816 has a 16-bit index, and there the
    /// processor state the file was laid out with says how wide it is.
    /// </summary>
    private static int? WidthOf(CodeLayout layout, Step load)
    {
        if (layout.Cpu != Cpu.Wdc65816 || layout.ReachesOneByte(load))
            return 8;
        return layout.Of(load.Statement, load.On)?.Bits == 16 ? 16 : null;
    }

    /// <summary>
    /// Marks the loop as counted, and returns what its blocks were marked with before. A loop
    /// inside another runs all of its iterations on each iteration of the outer one, so the
    /// counts multiply. The back edge is marked so that a walk of the routine stops at the latch
    /// rather than going round again.
    /// <para>
    /// Where the loop tests at the bottom, every block runs as many times as the test does.
    /// Where a jump enters it at the test, the body runs one time fewer than the test, because
    /// the last test falls out of the loop without running the body again.
    /// </para>
    /// </summary>
    private static (int? Iterations, int? Repeats, int? Trips)[] Mark(IReadOnlyList<BasicBlock> blocks, Loop loop, Counted counted)
    {
        var before = blocks.Select(block => (block.Iterations, block.Repeats, block.Trips)).ToArray();
        var first = counted.Test == loop.Header && counted.Test != loop.Latch;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!loop.Inside[i])
                continue;
            var runs = first && i != loop.Header ? counted.Trips - 1 : counted.Trips;
            blocks[i].Iterations = (blocks[i].Iterations ?? 1) * runs;
            if (i == loop.Latch)
            {
                blocks[i].Repeats = loop.Header;
                blocks[i].Trips = counted.Trips;
            }
        }
        return before;
    }

    /// <summary>
    /// Puts back the marks that <see cref="Mark"/> returned, for a loop whose cost turned out to
    /// be unknown.
    /// </summary>
    private static void Restore(IReadOnlyList<BasicBlock> blocks, (int? Iterations, int? Repeats, int? Trips)[] before)
    {
        for (var i = 0; i < blocks.Count; i++)
            (blocks[i].Iterations, blocks[i].Repeats, blocks[i].Trips) = before[i];
    }

    /// <summary>
    /// Returns what all the iterations of a loop cost, from what one iteration costs, or null
    /// where that is not known. The test's branch is taken every time but the last, which is the
    /// only difference between the final iteration and the others.
    /// </summary>
    private static CycleCount? Repeated(CodeLayout layout, IReadOnlyList<BasicBlock> blocks, Loop loop, Counted counted)
    {
        var iterations = counted.Trips;
        var test = blocks[counted.Test];
        var last = test.Steps.LastOrDefault(step => !step.IsMarker);
        if (layout.Of(last.Statement, last.On) is not { Branch: { } branch, Cycles: { } whole })
            return null;
        var (taken, notTaken) = branch;
        if (counted.Test == loop.Latch)
        {
            var (minimum, maximum, _) = Paths.Through(blocks, loop.Header, at => loop.Inside[at], Paths.Costing);
            if (minimum is not { } low || maximum is not { } high)
                return null;

            // The walk leaves the loop by the latch's branch not taken, so each bound holds that
            // once. Every iteration but the last takes the branch instead.
            return new CycleCount(
                ((low - notTaken.Minimum) * iterations) + (taken.Minimum * (iterations - 1)) + notTaken.Minimum,
                ((high - notTaken.Maximum) * iterations) + (taken.Maximum * (iterations - 1)) + notTaken.Maximum);
        }

        // A loop entered at its test runs the test every time and the body every time but the
        // last. The test block costs its own instructions each time, and its branch taken every
        // time but the last. The body is costed from where the taken branch goes to where the
        // latch goes back to the test.
        if (test.Cycles is not { } cycles)
            return null;
        var into = test.Successors.First(edge => edge.Kind == EdgeKind.Taken && edge.To != loop.Header && loop.Inside[edge.To]).To;
        var (fewest, most, _) = Paths.Through(blocks, into, at => loop.Inside[at] && at != loop.Header, Paths.Costing);
        if (fewest is not { } bodyLow || most is not { } bodyHigh)
            return null;
        var restLow = cycles.Minimum - whole.Minimum;
        var restHigh = cycles.Maximum - whole.Maximum;
        return new CycleCount(
            (restLow * iterations) + ((taken.Minimum + bodyLow) * (iterations - 1)) + notTaken.Minimum,
            (restHigh * iterations) + ((taken.Maximum + bodyHigh) * (iterations - 1)) + notTaken.Maximum);
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
        CodeLayout layout, BasicBlock block, Loop loop, Registers register,
        IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies) =>
        CallMayWrite(layout, block, register, bodies)
        || block.Successors.Any(edge => edge.Kind == EdgeKind.Call && !loop.Inside[edge.To]);

    /// <summary>
    /// Returns whether a call that <paramref name="block"/> makes to another routine, or to a place
    /// nt65 cannot name, may leave <paramref name="register"/> holding something else. A call to a
    /// label of this routine is not counted here.
    /// </summary>
    private static bool CallMayWrite(
        CodeLayout layout, BasicBlock block, Registers register, IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies) =>
        block.CallsUnknown
        || block.Calls.Any(callee => !LeftAlone(layout, callee, bodies, []).HasFlag(register));

    /// <summary>
    /// Returns the index registers that a call to <paramref name="callee"/> surely leaves as they
    /// were. Which registers a routine keeps is worked out only once the whole program has been
    /// analyzed, which is after loops are counted. So a routine is trusted only where its
    /// signature declares that it keeps a register, or where it has a body in this file that
    /// nothing in, and nothing it calls, writes that register.
    /// </summary>
    /// <param name="layout">The layout of the file.</param>
    /// <param name="callee">The routine called.</param>
    /// <param name="bodies">The blocks of every routine in the file.</param>
    /// <param name="visiting">
    /// The routines whose bodies are being read further up. A routine that reaches itself is
    /// trusted only as far as its signature says.
    /// </param>
    private static Registers LeftAlone(
        CodeLayout layout, Symbol callee, IReadOnlyDictionary<Symbol, IReadOnlyList<BasicBlock>> bodies, HashSet<Symbol> visiting)
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
                alone &= LeftAlone(layout, called, bodies, visiting);
            if (block.RunsInto is { } into)
                alone &= LeftAlone(layout, into, bodies, visiting);
            foreach (var step in InstructionsIn(block))
                alone &= ~WrittenBy(Mnemonic(step));
            alone &= ~HiddenWrites(layout, block);
        }
        visiting.Remove(callee);
        return declared | alone;
    }

    /// <summary>
    /// Returns the index registers the instruction at <paramref name="step"/> may change. A
    /// <c>rep</c> or <c>sep</c> whose immediate is known changes them only where it names the
    /// index-width bit.
    /// </summary>
    private static Registers WrittenBy(SemanticModel model, CodeLayout layout, Step step)
    {
        var mnemonic = Mnemonic(step);
        if (mnemonic is MnemonicKind.Rep or MnemonicKind.Sep
            && StepOperands.Immediate(model, layout, step) is { } mask && (mask & (long)StatusFlags.X) == 0)
        {
            return Registers.None;
        }
        return WrittenBy(mnemonic);
    }

    /// <summary>
    /// Returns the index registers an instruction may change. Changing the index width on the
    /// 65816 clears their high bytes, so an instruction that may do that counts as writing both.
    /// </summary>
    private static Registers WrittenBy(MnemonicKind mnemonic) =>
        mnemonic is MnemonicKind.Rep or MnemonicKind.Sep or MnemonicKind.Plp or MnemonicKind.Xce or MnemonicKind.Rti
            ? Registers.X | Registers.Y
            : Instructions.Facts(mnemonic).Writes & (Registers.X | Registers.Y);

    /// <summary>
    /// Returns the index registers that the instructions the bytes of a <see cref="HiddenPath"/>
    /// in <paramref name="block"/> decode as may change, as the same instructions written there
    /// would. The layout refuses to decode a <c>rep</c>, a <c>sep</c> or anything that pulls, so
    /// the mnemonic alone tells.
    /// </summary>
    private static Registers HiddenWrites(CodeLayout layout, BasicBlock block)
    {
        var written = Registers.None;
        foreach (var step in block.Steps)
        {
            if (layout.HiddenPathAt(step) is { } hidden)
            {
                foreach (var decoded in hidden.Instructions)
                    written |= WrittenBy(decoded.Mnemonic);
            }
        }
        return written;
    }

    /// <summary>Returns the instructions in a block, leaving out markers and directives.</summary>
    private static List<Step> InstructionsIn(BasicBlock block) =>
        [.. block.Steps.Where(step => !step.IsMarker && step.Statement is InstructionStatementSyntax)];

    /// <summary>Returns the mnemonic of a step's statement, or <see cref="MnemonicKind.None"/>.</summary>
    private static MnemonicKind Mnemonic(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.MnemonicKind ?? MnemonicKind.None;

    /// <summary>Represents a loop nt65 has counted.</summary>
    /// <param name="Trips">How many times the loop's test runs each time the loop is entered.</param>
    /// <param name="Test">The index of the block that holds the test.</param>
    private readonly record struct Counted(int Trips, int Test);
}
