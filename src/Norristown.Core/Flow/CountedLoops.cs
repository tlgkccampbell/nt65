using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// The loops nt65 can say how many turns of. How long most loops run is not in the program,
/// but the counted loop — a register loaded with an immediate, changed once a turn and
/// branched on — says it outright, and it is the loop the cycles go in.
/// <para>
/// Only one shape is read: a block that branches to itself, counting in X or Y, with nothing
/// else in it touching that register and one way in, which carries the immediate. Every other
/// loop keeps the bound it had, because a loop counted wrongly is worse than one not counted.
/// </para>
/// </summary>
internal static class CountedLoops
{
    /// <summary>What writes X, so that anything else in the loop touching it rules the loop out.</summary>
    private static readonly HashSet<string> WritesX =
        new(StringComparer.Ordinal) { "ldx", "tax", "tsx", "plx", "inx", "dex", "tyx" };

    /// <summary>The same for Y.</summary>
    private static readonly HashSet<string> WritesY =
        new(StringComparer.Ordinal) { "ldy", "tay", "ply", "iny", "dey", "txy" };

    /// <summary>Finds every counted loop in <paramref name="blocks"/> and writes what it costs on it.</summary>
    public static void Find(SemanticModel model, CodeLayout layout, IReadOnlyList<BasicBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (Turns(model, blocks, block) is not { } turns || Repeated(layout, block, turns) is not { } cost)
                continue;
            block.Turns = turns;
            block.LoopCycles = cost;
        }
    }

    /// <summary>
    /// How many turns <paramref name="block"/> takes as a loop of its own, or null when it is
    /// not one nt65 reads. Every step of the shape has to hold, so anything unexpected in it
    /// leaves the loop uncounted.
    /// </summary>
    private static int? Turns(SemanticModel model, IReadOnlyList<BasicBlock> blocks, BasicBlock block)
    {
        if (!block.Successors.Any(edge => edge.Kind == EdgeKind.Taken && edge.To == block.Index))
            return null;

        // The branch that closes the loop, and the counter it branches on, which is the
        // statement before it: anything between the two would set the flags instead.
        var steps = block.Steps.Where(step => !step.IsMarker && step.Statement.Kind == SyntaxKind.InstructionStatement).ToList();
        if (steps.Count < 2)
            return null;
        var branch = Mnemonic(steps[^1]);
        var counter = Mnemonic(steps[^2]);
        if (branch is not ("bne" or "bpl") || counter is not ("dex" or "dey"))
            return null;
        var register = counter == "dex" ? WritesX : WritesY;

        // Nothing else in the loop may touch the register, or how far it has come is not the
        // immediate the loop started from.
        for (var i = 0; i < steps.Count - 2; i++)
        {
            if (Mnemonic(steps[i]) is { } other && register.Contains(other))
                return null;
        }

        // One way in, carrying the immediate the count starts at.
        var from = block.Predecessors.Where(at => at != block.Index).Distinct().ToList();
        if (from.Count != 1 || Started(model, blocks[from[0]], counter == "dex" ? "ldx" : "ldy", register) is not { } start)
            return null;

        // `bne` runs the count down to zero, and `bpl` one turn past it. A count of zero
        // turns on how wide the register is, which is not always known, so it is left alone.
        var turns = branch == "bne" ? start : start + 1;
        return start > 0 && turns is > 0 and <= 0x10000 ? (int)turns : null;
    }

    /// <summary>
    /// The immediate the block before the loop leaves in the register, or null when what it
    /// leaves there is anything else. The last thing it writes is what the loop starts from.
    /// </summary>
    private static long? Started(SemanticModel model, BasicBlock before, string load, HashSet<string> register)
    {
        long? started = null;
        foreach (var step in before.Steps)
        {
            if (step.IsMarker || step.Statement.Kind != SyntaxKind.InstructionStatement || Mnemonic(step) is not { } mnemonic)
                continue;
            if (!register.Contains(mnemonic))
                continue;
            started = mnemonic == load ? Immediate(model, step) : null;
        }
        return started;
    }

    /// <summary>
    /// What running the block <paramref name="turns"/> times costs. The branch is taken every
    /// turn but the last, which is the one difference between a turn and the turn that leaves.
    /// </summary>
    private static CycleCount? Repeated(CodeLayout layout, BasicBlock block, int turns)
    {
        if (block.Cycles is not { } once)
            return null;
        var last = block.Steps.LastOrDefault(step => !step.IsMarker);
        if (layout.Of(last.Statement, last.On)?.Cycles is not { } branch)
            return null;

        // A branch costs its fewest when it is not taken, and one more than that when it is;
        // a taken one that crosses a page costs the most of all.
        var rest = new CycleCount(once.Least - branch.Least, once.Most - branch.Most);
        var taken = new CycleCount(branch.Least + 1, branch.Most);
        return new CycleCount(
            (rest.Least * turns) + (taken.Least * (turns - 1)) + branch.Least,
            (rest.Most * turns) + (taken.Most * (turns - 1)) + branch.Least);
    }

    /// <summary>The mnemonic a step's statement is written with, lower case, or null.</summary>
    private static string? Mnemonic(Step step) =>
        step.Statement.ChildTokens.Length > 0 ? step.Statement.ChildTokens[0].Text.ToLowerInvariant() : null;

    /// <summary>The value of the immediate a step is written with, or null when it has none nt65 knows.</summary>
    private static long? Immediate(SemanticModel model, Step step) =>
        step.Statement.ChildNodes.FirstOrDefault() is { Kind: SyntaxKind.ImmediateOperand } operand
            && operand.ChildNodes.FirstOrDefault() is { } expression
            ? model.ValueOf(expression, step.On).AsNumber()
            : null;
}
