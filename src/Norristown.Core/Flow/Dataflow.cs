namespace Norristown.Flow;

/// <summary>
/// Runs a routine's basic blocks to a fixed point over some kind of state. The processor-state
/// and register analyses both use it, and differ only in their state, in what a block does to
/// it and in how two states meet.
/// <para>
/// Blocks are taken lowest index first, which is the order their bytes are emitted in, so most
/// of a routine reaches its fixed point in a single pass. A block is walked again whenever the
/// state reaching it changes.
/// </para>
/// </summary>
/// <typeparam name="TState">The state that flows through the blocks.</typeparam>
internal sealed class Dataflow<TState>
    where TState : class, IEquatable<TState>
{
    private readonly IReadOnlyList<BasicBlock> blocks;
    private readonly Func<BasicBlock, TState, TState> transfer;
    private readonly Func<TState?, TState, TState> merge;
    private readonly Func<BasicBlock, IEnumerable<int>> successors;
    private readonly SortedSet<int> pending = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="Dataflow{TState}"/> class over
    /// <paramref name="blocks"/>, in which nothing has been reached yet.
    /// </summary>
    /// <param name="blocks">The routine's blocks, each at its own index.</param>
    /// <param name="transfer">Returns the state after a block, from the state that reaches it.</param>
    /// <param name="merge">Returns what is known where a state arrives at a block that another may already reach.</param>
    /// <param name="successors">Returns the blocks the state after a block flows to.</param>
    public Dataflow(
        IReadOnlyList<BasicBlock> blocks, Func<BasicBlock, TState, TState> transfer,
        Func<TState?, TState, TState> merge, Func<BasicBlock, IEnumerable<int>> successors)
    {
        this.blocks = blocks;
        this.transfer = transfer;
        this.merge = merge;
        this.successors = successors;
        Reached = new TState?[blocks.Count];
    }

    /// <summary>
    /// Gets the state reaching each block, at the block's index, or null for a block that
    /// nothing has reached.
    /// </summary>
    public TState?[] Reached { get; }

    /// <summary>
    /// Enters the block at <paramref name="index"/> with <paramref name="state"/>, and runs
    /// everything it reaches to a fixed point.
    /// </summary>
    public void Enter(int index, TState state)
    {
        Reached[index] = state;
        pending.Add(index);
        Converge();
    }

    /// <summary>
    /// Enters each label that a <c>.state</c> declares, which is an entry point in its own right,
    /// and runs what it reaches to a fixed point. A label no path reaches starts from
    /// <paramref name="unreached"/>, or is left unreached where that is null. A label that some path reaches and that
    /// <paramref name="outside"/> finds can also be entered from outside the routine starts from
    /// what <paramref name="entered"/> makes of the state the path brings. Any other label keeps
    /// what reaches it.
    /// </summary>
    public void EnterDeclared(OutsideEntries outside, TState? unreached, Func<BasicBlock, TState, TState> entered)
    {
        foreach (var block in blocks)
        {
            if (!block.IsDeclared)
                continue;
            var here = Reached[block.Index];
            TState state;
            if (here is null)
            {
                if (unreached is null)
                    continue;
                state = unreached;
            }
            else if (outside.Reaches(block))
                state = entered(block, here);
            else
                continue;
            if (state.Equals(here))
                continue;
            Enter(block.Index, state);
        }
    }

    private void Converge()
    {
        while (pending.Count > 0)
        {
            var index = pending.Min;
            pending.Remove(index);
            var block = blocks[index];
            var after = transfer(block, Reached[index]!);
            foreach (var to in successors(block))
            {
                var merged = merge(Reached[to], after);
                if (merged.Equals(Reached[to]))
                    continue;
                Reached[to] = merged;
                pending.Add(to);
            }
        }
    }
}
