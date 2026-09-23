using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// A run of statements that is entered only at its first and left only at its last, so if any
/// of it runs, all of it runs. A label starts a new block, and a statement that transfers
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

    /// <summary>The block's index among its routine's blocks.</summary>
    public int Index { get; }

    /// <summary>The label it starts at, or null for a block nothing names.</summary>
    public Symbol? Label { get; }

    /// <summary>Which writing of the lines this block is, or null outside every expansion.</summary>
    public Expansion? On { get; }

    /// <summary>Which stream of bytes it is in: its routine's own, or a nested segment block's.</summary>
    public int Stream { get; }

    /// <summary>The statements in it, in order.</summary>
    public IReadOnlyList<Step> Steps => steps;

    /// <summary>The blocks that may run after it.</summary>
    public IReadOnlyList<FlowEdge> Successors => successors;

    /// <summary>The blocks it may be reached from.</summary>
    public IReadOnlyList<int> Predecessors => predecessors;

    /// <summary>
    /// The routine the block's call names, if it makes a call nt65 can follow. A call ends a
    /// block, so there is at most one, and what the called routine costs is counted as that
    /// routine's own cost rather than this block's.
    /// </summary>
    public IReadOnlyList<Symbol> Calls => calls;

    /// <summary>
    /// Whether it calls somewhere nt65 cannot identify: through a pointer, or at an address no
    /// declaration is placed at. What such a call costs cannot be worked out from the program.
    /// </summary>
    public bool CallsUnknown { get; internal set; }

    /// <summary>
    /// Whether the block above falls through into it, as opposed to control reaching it only
    /// by a transfer to its label.
    /// </summary>
    public bool IsFallenInto { get; internal set; }

    /// <summary>Whether any path from the routine's entry reaches it.</summary>
    public bool IsReached { get; internal set; }

    /// <summary>
    /// The <c>.next</c> written under the block's last statement, or null. It says where
    /// control goes after that statement, in place of what its operand says.
    /// </summary>
    public NextDirectiveSyntax? Next { get; internal set; }

    /// <summary>
    /// The routine a <c>.fallthrough</c> ending the block names, or null. The block is then the
    /// end of the routine's body, and every path through it runs on into that routine.
    /// </summary>
    public Symbol? RunsInto { get; internal set; }

    /// <summary>
    /// Whether a <c>.state</c> stands directly after the block's label, which declares the
    /// label an entry point: the state there is what the directive says. One carrying nothing
    /// but <c>keeps</c> is not such a declaration, because it says what a register holds and
    /// not what the processor state at the label is.
    /// </summary>
    public bool IsDeclared => Label is not null && steps.Count > 0
        && steps[0].Statement is StateDirectiveSyntax state
        && !Semantics.StateItem.OnlyKeeps(state);

    /// <summary>
    /// How long running the whole block takes, or null when any statement in it has no
    /// count. A block runs all of it or none, so the sum is a bound anyone can use.
    /// </summary>
    public CycleCount? Cycles { get; internal set; }

    /// <summary>
    /// Why it has no count, in a sentence, where nt65 knows the instruction but still cannot
    /// say how long it takes; null where it has a count, and where the uncounted line is one
    /// nt65 could not lay out at all.
    /// </summary>
    public string? Uncounted { get; internal set; }

    /// <summary>
    /// How many times one pass through the routine runs it, where it is a loop counting a
    /// register down from an immediate; null for every other block, whose iteration count
    /// the program does not state.
    /// </summary>
    public int? Turns { get; internal set; }

    /// <summary>What running it all of those iterations costs, or null where they are not known.</summary>
    public CycleCount? LoopCycles { get; internal set; }

    /// <summary>
    /// For the latch of a counted loop, the index of the loop's header block, which the latch
    /// branches back to; a walk stops at the latch rather than going round again. Null for
    /// every other block.
    /// </summary>
    public int? Repeats { get; internal set; }

    internal void Add(Step step) => steps.Add(step);

    internal void Called(Symbol routine) => calls.Add(routine);

    /// <summary>
    /// Records a call whose target may not have resolved to any routine; an unresolved one
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
