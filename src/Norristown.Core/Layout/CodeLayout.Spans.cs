using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    // This part counts a cycle span, which is the value of `.mincycles` and `.maxcycles`.

    /// <summary>
    /// Returns the cost in cycles of a pass from <paramref name="from"/> to <paramref name="to"/>,
    /// as the lower bound or, when <paramref name="upperBound"/> is true, the upper bound. The two
    /// must be positions in one routine and one segment block.
    /// <para>
    /// A pass starts at <paramref name="from"/> and follows the code as it runs, so a branch may go
    /// either way and a jump goes to its target. It ends where it first arrives at
    /// <paramref name="to"/>, which is where the pass arrives rather than an instruction it runs.
    /// The lower bound is the cheapest pass that arrives, and the upper bound is the most expensive.
    /// </para>
    /// <para>
    /// A path that returns, or leaves the routine, before it arrives at <paramref name="to"/> is
    /// not a pass, so it costs nothing here. A span that no path from <paramref name="from"/>
    /// arrives at has no count.
    /// </para>
    /// <para>
    /// A branch costs its fewest cycles where it is not taken, and from one more up to its most
    /// where it is taken, because only a taken branch can cross a page. A long branch laid out as
    /// an inverted branch over a <c>jmp</c> costs the inverted branch taken where its condition
    /// fails, and the inverted branch not taken plus the <c>jmp</c> where it holds.
    /// </para>
    /// <para>
    /// A call takes as long as the routine it calls, and a loop runs its body as many times as it
    /// iterates. So a span has no count when a path from <paramref name="from"/> to
    /// <paramref name="to"/> makes a call, or can come back on itself. A transfer whose target nt65
    /// cannot follow might go anywhere, so any such transfer a pass can reach leaves the span
    /// without a count as well. The result gives the reason instead.
    /// </para>
    /// <para>
    /// A layout that no cycle span asked about while it was laid out has no completed walk to
    /// count over, and returns no count.
    /// </para>
    /// </summary>
    public CycleSpan CyclesOf(Symbol from, Symbol to, bool upperBound)
    {
        if (counted is null)
            return default;
        if (At(from) is not { } start)
            return new CycleSpan(null, $"`{from.DisplayName}` is not in any laid-out code");
        if (At(to) is not { } end)
            return new CycleSpan(null, $"`{to.DisplayName}` is not in any laid-out code");
        if (counted[start].Routine is not { } routine || counted[end].Routine != routine)
            return new CycleSpan(null, "the two positions are in different routines");
        if (counted[start].Stream != counted[end].Stream)
            return new CycleSpan(null, "the two positions are in different segment blocks");
        var stream = counted[start].Stream;

        // A transfer reaches the step of the label it names. A label outside this routine's part
        // of the stream is somewhere a pass leaves for, and never arrives back from.
        var labelled = new Dictionary<(Symbol Symbol, Expansion? At), int>();
        for (var i = 0; i < counted.Count; i++)
        {
            if (counted[i] is { Label: { } label } step && step.Routine == routine && step.Stream == stream)
                labelled.TryAdd((label, Expansion.Owning(step.On, label)), i);
        }

        // Every step a pass can run is found from the start, without going past the end.
        var edges = new Dictionary<int, List<SpanEdge>>();
        var lost = new SortedDictionary<int, string>();
        var unbounded = new SortedDictionary<int, string>();
        var reached = new HashSet<int> { start };
        var pending = new Queue<int>();
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            var at = pending.Dequeue();
            if (at == end)
                continue;
            var onward = Onward(at, routine, stream, labelled);
            edges[at] = onward.Edges;
            if (onward.Lost is { } why)
                lost[at] = why;
            if (onward.Unbounded is { } reason)
                unbounded[at] = reason;
            foreach (var edge in onward.Edges)
            {
                if (reached.Add(edge.To))
                    pending.Enqueue(edge.To);
            }
        }

        // A transfer nt65 cannot follow might reach the end by any route, so it is checked before
        // the question of whether the end is reached at all.
        if (lost.Count > 0)
            return new CycleSpan(null, lost.First().Value);
        if (!reached.Contains(end))
            return new CycleSpan(null, $"no path from `{from.DisplayName}` arrives at `{to.DisplayName}`");

        // Only the steps on a path that arrives are part of a pass. A call or a missing count on a
        // path that returns first is no part of what the span measures.
        var arriving = Arriving(edges, end);
        foreach (var (at, reason) in unbounded)
        {
            if (arriving.Contains(at))
                return new CycleSpan(null, reason);
        }

        if (Bounds(edges, arriving, start, end) is not { } bounds)
            return new CycleSpan(null, Loop(edges, arriving));
        return new CycleSpan(upperBound ? bounds.Maximum : bounds.Minimum, null);
    }

    /// <summary>
    /// Returns the steps a pass can go on to from step <paramref name="i"/>, with what each costs.
    /// It also returns why a transfer there cannot be followed, and why a pass through it has no
    /// count, where either applies.
    /// </summary>
    private (List<SpanEdge> Edges, string? Lost, string? Unbounded) Onward(
        int i, Symbol routine, int stream, Dictionary<(Symbol Symbol, Expansion? At), int> labelled)
    {
        var step = counted![i];
        var line = Of(step.Statement, step.On);
        var edges = new List<SpanEdge>();
        if (step.Statement is not InstructionStatementSyntax instruction || step.Label is not null)
        {
            // Labels and markers take no time. A statement that is no instruction costs what its
            // layout says, which is what an `.ensure` emits, and nothing otherwise.
            var free = step.Label is not null || step.IsMarker || step.Statement is StateDirectiveSyntax or FrameDirectiveSyntax;
            Next(free ? new CycleCount(0) : line?.Cycles ?? new CycleCount(0));
            return (edges, null, null);
        }

        var mode = line?.Mode;
        var transfer = Transfers.Of(instruction, mode);
        var mnemonic = SyntaxFacts.TextOf(instruction.MnemonicKind);
        string? unbounded = null;
        if (Instructions.IsCall(instruction.MnemonicKind))
            unbounded = $"the span contains a call, `{mnemonic}`, whose time depends on the routine it calls";
        else if (transfer == Transfer.Elsewhere)
            return (edges, $"the span contains `{mnemonic}`, whose target nt65 cannot follow", null);
        else if (line?.Cycles is null)
            unbounded = $"nt65 has no cycle count for `{mnemonic}`";

        var cycles = line?.Cycles ?? new CycleCount(0);
        switch (transfer)
        {
            case Transfer.Return:
                break;
            case Transfer.Jump:
                Target(cycles);
                break;
            case Transfer.Branch when cycles.IsExact:
                Target(cycles);
                Next(cycles);
                break;
            case Transfer.Branch when line?.Inverted == true:
                Target(new CycleCount(cycles.Maximum));
                Next(new CycleCount(cycles.Minimum, cycles.Maximum - 1));
                break;
            case Transfer.Branch:
                Target(new CycleCount(cycles.Minimum + 1, cycles.Maximum));
                Next(new CycleCount(cycles.Minimum));
                break;
            default:
                Next(cycles);
                break;
        }
        return (edges, null, unbounded);

        void Next(CycleCount cost)
        {
            if (i + 1 < counted.Count && counted[i + 1].Routine == routine && counted[i + 1].Stream == stream)
                edges.Add(new SpanEdge(i + 1, cost));
        }

        void Target(CycleCount cost)
        {
            if (Targets.Of(model, Transfers.TargetOf(instruction, mode), step.On) is { } target
                && labelled.TryGetValue(target, out var to))
            {
                edges.Add(new SpanEdge(to, cost));
            }
        }
    }

    /// <summary>
    /// Returns the steps among <paramref name="edges"/> from which a path arrives at
    /// <paramref name="end"/>, the end included.
    /// </summary>
    private static HashSet<int> Arriving(Dictionary<int, List<SpanEdge>> edges, int end)
    {
        var into = new Dictionary<int, List<int>>();
        foreach (var (at, onward) in edges)
        {
            foreach (var edge in onward)
            {
                if (!into.TryGetValue(edge.To, out var sources))
                    into[edge.To] = sources = [];
                sources.Add(at);
            }
        }

        var arriving = new HashSet<int> { end };
        var pending = new Queue<int>();
        pending.Enqueue(end);
        while (pending.Count > 0)
        {
            foreach (var at in into.GetValueOrDefault(pending.Dequeue()) ?? [])
            {
                if (arriving.Add(at))
                    pending.Enqueue(at);
            }
        }
        return arriving;
    }

    /// <summary>
    /// Returns the cheapest and the most expensive cost of a path from <paramref name="start"/> to
    /// <paramref name="end"/> through the steps in <paramref name="arriving"/>, or null when such a
    /// path can come back on itself. The steps are put in the order they can run in, and a loop is
    /// what is left over when nothing can be put next.
    /// </summary>
    private static CycleCount? Bounds(Dictionary<int, List<SpanEdge>> edges, HashSet<int> arriving, int start, int end)
    {
        var waiting = arriving.ToDictionary(at => at, _ => 0);
        foreach (var at in arriving)
        {
            foreach (var edge in edges.GetValueOrDefault(at) ?? [])
            {
                if (arriving.Contains(edge.To))
                    waiting[edge.To]++;
            }
        }

        var low = new Dictionary<int, int> { [start] = 0 };
        var high = new Dictionary<int, int> { [start] = 0 };
        var pending = new Queue<int>(arriving.Where(at => waiting[at] == 0));
        var placed = 0;
        while (pending.Count > 0)
        {
            var at = pending.Dequeue();
            placed++;
            foreach (var edge in edges.GetValueOrDefault(at) ?? [])
            {
                if (!arriving.Contains(edge.To))
                    continue;
                if (low.TryGetValue(at, out var fewest))
                {
                    var cheap = fewest + edge.Cost.Minimum;
                    var dear = high[at] + edge.Cost.Maximum;
                    low[edge.To] = low.TryGetValue(edge.To, out var was) ? Math.Min(was, cheap) : cheap;
                    high[edge.To] = high.TryGetValue(edge.To, out was) ? Math.Max(was, dear) : dear;
                }
                if (--waiting[edge.To] == 0)
                    pending.Enqueue(edge.To);
            }
        }
        return placed == arriving.Count ? new CycleCount(low[end], high[end]) : null;
    }

    /// <summary>
    /// Returns why the steps in <paramref name="arriving"/> form a loop. Control only falls
    /// forward, so a loop holds a branch or a jump back to a step at or before itself, and the
    /// first such transfer is the one named.
    /// </summary>
    private string Loop(Dictionary<int, List<SpanEdge>> edges, HashSet<int> arriving)
    {
        foreach (var at in arriving.Order())
        {
            foreach (var edge in edges.GetValueOrDefault(at) ?? [])
            {
                if (edge.To <= at && arriving.Contains(edge.To) && counted![at].Statement is InstructionStatementSyntax instruction)
                {
                    var target = counted[edge.To].Label?.DisplayName ?? "an earlier position";
                    return $"`{SyntaxFacts.TextOf(instruction.MnemonicKind)}` loops back to `{target}`";
                }
            }
        }
        return "the span contains a loop";
    }

    /// <summary>
    /// Returns the index of the completed walk's step at which a symbol stands. That is the step
    /// that declares it as a label or, for a routine's own name, the routine's first step. Returns
    /// null for a symbol the walk did not reach.
    /// </summary>
    private int? At(Symbol symbol)
    {
        for (var i = 0; i < counted!.Count; i++)
        {
            if (counted[i].Label == symbol || (symbol.Kind == SymbolKind.Proc && counted[i].Routine == symbol))
                return i;
        }
        return null;
    }

    /// <summary>Represents one way a pass can go on from a step, and what going that way costs.</summary>
    /// <param name="To">The index of the step the pass goes on to.</param>
    /// <param name="Cost">The cycles the step costs when the pass leaves it this way.</param>
    private readonly record struct SpanEdge(int To, CycleCount Cost);
}
