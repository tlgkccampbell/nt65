using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// A run of statements that is entered only at its first and left only at its last: what
/// runs, runs all of it. A label starts one, and a statement that transfers control ends
/// one.
/// </summary>
public sealed class BasicBlock
{
    private readonly List<Step> steps = [];
    private readonly List<FlowEdge> successors = [];
    private readonly List<int> predecessors = [];

    internal BasicBlock(int index, Symbol? label, Expansion? on, int stream)
    {
        Index = index;
        Label = label;
        On = on;
        Stream = stream;
    }

    /// <summary>Which block it is, among its routine's.</summary>
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

    /// <summary>Whether anything runs into it from above rather than naming it.</summary>
    public bool IsFallenInto { get; internal set; }

    /// <summary>Whether any path from the routine's entry reaches it.</summary>
    public bool IsReached { get; internal set; }

    /// <summary>
    /// The <c>.next</c> written under the block's last statement, or null. It says where
    /// control goes after that statement, in place of what its operand says.
    /// </summary>
    public SyntaxNode? Next { get; internal set; }

    /// <summary>
    /// Whether a <c>.state</c> stands directly after the block's label, which declares the
    /// label an entry point: the state there is what the directive says.
    /// </summary>
    public bool IsDeclared => Label is not null && steps.Count > 0
        && steps[0].Statement.Kind == SyntaxKind.StateDirective;

    /// <summary>
    /// How long running the whole block takes, or null when any statement in it has no
    /// count. A block runs all of it or none, so the sum is a bound anyone can use.
    /// </summary>
    public CycleCount? Cycles { get; internal set; }

    internal void Add(Step step) => steps.Add(step);

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
