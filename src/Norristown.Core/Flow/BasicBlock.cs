using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents a run of statements that is entered only at its first statement and left only at
/// its last, so if any of it runs, all of it runs. A label starts a new block, and a statement that transfers
/// control ends one.
/// </summary>
public sealed class BasicBlock
{
    private readonly List<Step> steps = [];
    private readonly List<FlowEdge> successors = [];
    private readonly List<int> predecessors = [];
    private readonly List<Symbol> calls = [];

    internal BasicBlock(int index, Symbol? label, Expansion? on, int stream)
    {
        Index = index;
        Label = label;
        On = on;
        Stream = stream;
    }

    /// <summary>Gets the block's index among its routine's blocks.</summary>
    public int Index { get; }

    /// <summary>Gets the label the block starts at, or null for a block nothing names.</summary>
    public Symbol? Label { get; }

    /// <summary>
    /// Gets the <see cref="Expansion"/> this block belongs to, or null outside every expansion.
    /// </summary>
    public Expansion? On { get; }

    /// <summary>
    /// Gets which stream of bytes the block is in, which is either its routine's own stream or a
    /// nested segment block's.
    /// </summary>
    public int Stream { get; }

    /// <summary>Gets the statements in the block, in order.</summary>
    public IReadOnlyList<Step> Steps => steps;

    /// <summary>Gets the edges to the blocks that may run after this one.</summary>
    public IReadOnlyList<FlowEdge> Successors => successors;

    /// <summary>Gets the indexes of the blocks this one may be reached from.</summary>
    public IReadOnlyList<int> Predecessors => predecessors;

    /// <summary>
    /// Gets the routine the block's call names, if it makes a call nt65 can follow. A call ends a
    /// block, so there is at most one. What the called routine costs is counted as that routine's
    /// own cost rather than this block's.
    /// </summary>
    public IReadOnlyList<Symbol> Calls => calls;

    /// <summary>
    /// Gets a value indicating whether the block calls somewhere nt65 cannot identify. Such a call
    /// goes through a pointer, or to an address where no declaration is. What such a call costs
    /// cannot be worked out from the program.
    /// </summary>
    public bool CallsUnknown { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the block above falls through into this one, as opposed to
    /// control reaching it only by a transfer to its label.
    /// </summary>
    public bool IsFallenInto { get; internal set; }

    /// <summary>Gets a value indicating whether any path from the routine's entry reaches the block.</summary>
    public bool IsReached { get; internal set; }

    /// <summary>
    /// Gets the <c>.next</c> under the block's last statement, or null. It says where control goes
    /// after that statement, in place of what the statement's operand says.
    /// </summary>
    public NextDirectiveSyntax? Next { get; internal set; }

    /// <summary>
    /// Gets the routine a <c>.fallthrough</c> ending the block names, or null. The block is then
    /// the end of the routine's body, and every path through it runs on into that routine.
    /// </summary>
    public Symbol? RunsInto { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether a <c>.state</c> stands directly after the block's label,
    /// declaring the label an entry point whose state is what the directive says. A
    /// <c>.state</c> that contains nothing but <c>keeps</c> is not such a declaration, because it
    /// says what a register holds and not what the processor state at the label is.
    /// </summary>
    public bool IsDeclared => Label is not null && steps.Count > 0
        && steps[0].Statement is StateDirectiveSyntax state
        && !Semantics.StateItem.OnlyKeeps(state);

    /// <summary>
    /// Gets how long running the whole block takes, or null when any statement in it has no
    /// count. A block runs all of its statements or none, so the sum is a bound anyone can use.
    /// </summary>
    public CycleCount? Cycles { get; internal set; }

    /// <summary>
    /// Gets a sentence saying why the block has no count, where nt65 knows the instruction but
    /// still cannot say how long it takes. It is null where the block has a count, and where the
    /// uncounted line is one nt65 could not lay out at all.
    /// </summary>
    public string? Uncounted { get; internal set; }

    /// <summary>
    /// Gets how many times one pass through the routine runs the block, where it is a loop
    /// counting a register down from an immediate. It is null for every other block, whose
    /// iteration count the program does not state.
    /// </summary>
    public int? Iterations { get; internal set; }

    /// <summary>
    /// Gets what running the block for all of those iterations costs, or null where they are not
    /// known.
    /// </summary>
    public CycleCount? LoopCycles { get; internal set; }

    /// <summary>
    /// Gets, for the latch of a counted loop, the index of the loop's header block, which the
    /// latch branches back to. A walk stops at the latch rather than going round again. It is null
    /// for every other block.
    /// </summary>
    public int? Repeats { get; internal set; }

    internal void Add(Step step) => steps.Add(step);

    internal void Called(Symbol routine) => calls.Add(routine);

    /// <summary>
    /// Records a call whose target may not have resolved to any routine. An unresolved target
    /// sets <see cref="CallsUnknown"/>.
    /// </summary>
    internal void SetCalled(Symbol? routine)
    {
        if (routine is null)
            CallsUnknown = true;
        else
            calls.Add(routine);
    }

    internal void Reach(int to, EdgeKind kind)
    {
        if (!successors.Contains(new FlowEdge(to, kind)))
            successors.Add(new FlowEdge(to, kind));
    }

    internal void ReachedFrom(int from)
    {
        if (!predecessors.Contains(from))
            predecessors.Add(from);
    }
}
