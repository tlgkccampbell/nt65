using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Where control goes inside each routine: its basic blocks and the edges between them,
/// built from the order layout wrote the bytes in, so the macros are expanded and the
/// repetitions unrolled before anything is asked about the path.
/// <para>
/// Assembly's tricks are all allowed, and each has a syntactic fingerprint. Where the
/// operand does not say where flow goes — an indirect jump, a computed target — the edge
/// comes from a <c>.next</c> instead, and where there is none the path simply ends. On the
/// 6502 and the 65C02 nothing consumes processor state, so no annotation is required.
/// </para>
/// </summary>
public sealed class ControlFlow
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly List<FlowRegion> regions = [];

    private ControlFlow(SemanticModel model, CodeLayout layout)
    {
        this.model = model;
        this.layout = layout;
    }

    /// <summary>Every region of every routine in the file.</summary>
    public IReadOnlyList<FlowRegion> Regions => regions;

    /// <summary>What is wrong with the paths through this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Works out where control goes in <paramref name="layout"/>'s file.</summary>
    public static ControlFlow Of(SemanticModel model, CodeLayout layout)
    {
        var flow = new ControlFlow(model, layout);
        var diagnostics = new List<Diagnostic>();

        // A routine's bytes are one stream unless a nested segment block takes some of them
        // somewhere else, and each stream is a path of its own.
        foreach (var run in layout.Steps.Where(step => step.Routine is not null)
            .GroupBy(step => (step.Routine, step.Stream)))
        {
            var routine = run.Key.Routine!;
            var units = Units([.. run]);
            var blocks = flow.Blocks(units);

            // The routine is entered where its own name stands; a region that is a detour
            // into another segment is entered by nothing fall-through can see.
            var entered = blocks.Count > 0 && blocks[0].Label == routine;
            var region = new FlowRegion(routine, run.Key.Stream, entered, blocks);
            flow.regions.Add(region);
            flow.CheckUnreachableLabels(region, diagnostics);
            flow.CheckDataReachedByFallingThrough(units, diagnostics);
        }

        flow.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
        return flow;
    }

    /// <summary>
    /// The statements of one region, each with the annotations written under it. An
    /// annotation is about the statement above it, so it belongs to that statement rather
    /// than standing on its own.
    /// </summary>
    private static List<Unit> Units(IReadOnlyList<Step> steps)
    {
        var units = new List<Unit>();
        foreach (var step in steps)
        {
            if (Annotations.Is(step.Statement) && units.Count > 0)
            {
                units[^1].Annotations.Add(step.Statement);
                continue;
            }
            units.Add(new Unit(step));
        }
        return units;
    }

    /// <summary>
    /// The blocks of one region. A label starts a block, and a statement that transfers
    /// control ends one, so what runs of a block runs all of it.
    /// </summary>
    private List<BasicBlock> Blocks(IReadOnlyList<Unit> units)
    {
        var blocks = new List<BasicBlock>();
        var tails = new List<Unit?>();
        var fallenInto = new List<bool>();
        var found = new Dictionary<(Symbol Symbol, Expansion? At), int>();

        // Whether the block being built can still take statements, and whether the one
        // before it ran off its end into whatever comes next.
        var open = false;
        var runsOn = false;

        foreach (var unit in units)
        {
            if (unit.Step.Label is { } label)
            {
                Open(label, unit.Step.On);
                found.TryAdd((label, Expansion.Owning(unit.Step.On, label)), blocks.Count - 1);
                continue;
            }
            if (!open)
                Open(null, unit.Step.On);

            blocks[^1].Add(unit.Step);
            tails[^1] = unit;
            if (!EndsBlock(unit))
                continue;
            open = false;
            runsOn = RunsOn(unit);
        }

        Link(blocks, tails, fallenInto, found);
        Reach(blocks);
        for (var i = 0; i < blocks.Count; i++)
            blocks[i].IsFallenInto = fallenInto[i];
        return blocks;

        void Open(Symbol? label, Expansion? on)
        {
            blocks.Add(new BasicBlock(blocks.Count, label, on));
            tails.Add(null);
            fallenInto.Add(runsOn);
            open = true;
            runsOn = true;
        }
    }

    /// <summary>
    /// The edges. Fall-through is already known from how the blocks were cut; what is left
    /// is where each block's last statement says control goes.
    /// </summary>
    private void Link(
        List<BasicBlock> blocks, List<Unit?> tails, List<bool> fallenInto,
        Dictionary<(Symbol Symbol, Expansion? At), int> found)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i + 1 < blocks.Count && fallenInto[i + 1])
                Edge(i, i + 1, EdgeKind.FallThrough);
            if (tails[i] is not { } tail)
                continue;

            // A `.next` replaces what the operand says, because the operand does not say it.
            // A call is the exception: it lists where the call goes, and the call still
            // returns to the statement after it.
            if (tail.Next is { } next)
            {
                foreach (var named in Named(next, tail.Step.On))
                {
                    if (found.TryGetValue(named, out var to))
                        Edge(i, to, EdgeKind.Declared);
                }
                continue;
            }

            var mode = layout.Of(tail.Step.Statement, tail.Step.On)?.Mode;
            var transfer = Transfers.Of(tail.Step.Statement, mode);
            if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call))
                continue;
            if (Targets.Of(model, Transfers.TargetOf(tail.Step.Statement, mode), tail.Step.On) is { } target
                && found.TryGetValue(target, out var reached))
            {
                Edge(i, reached, transfer == Transfer.Call ? EdgeKind.Call : EdgeKind.Taken);
            }
        }

        void Edge(int from, int to, EdgeKind kind)
        {
            blocks[from].Reach(to, kind);
            blocks[to].ReachedFrom(from);
        }
    }

    /// <summary>Which blocks any path from the region's first one reaches.</summary>
    private static void Reach(List<BasicBlock> blocks)
    {
        if (blocks.Count == 0)
            return;
        var pending = new Queue<int>();
        pending.Enqueue(0);
        blocks[0].IsReached = true;
        while (pending.Count > 0)
        {
            foreach (var edge in blocks[pending.Dequeue()].Successors)
            {
                if (blocks[edge.To].IsReached)
                    continue;
                blocks[edge.To].IsReached = true;
                pending.Enqueue(edge.To);
            }
        }
    }

    /// <summary>
    /// The labels one <c>.next</c> names. A target that is a list, or a data label whose
    /// items are all code labels — each optionally minus one, as an RTS dispatch table
    /// writes them — stands for every one of those labels.
    /// </summary>
    private IEnumerable<(Symbol Symbol, Expansion? At)> Named(SyntaxNode next, Expansion? on)
    {
        foreach (var written in Annotations.TargetsOf(next))
        {
            if (Targets.Of(model, written, on) is not { } target)
                continue;
            var spread = Spread(target.Symbol, on).ToList();
            if (spread.Count > 0)
            {
                foreach (var item in spread)
                    yield return item;
                continue;
            }
            yield return target;
        }
    }

    /// <summary>The labels a table or a list stands for, empty when it stands only for itself.</summary>
    private IEnumerable<(Symbol Symbol, Expansion? At)> Spread(Symbol target, Expansion? on)
    {
        var items = target.Kind == SymbolKind.List ? target.Items : ItemsOfTable(target);
        foreach (var item in items)
        {
            if (Targets.Of(model, Stripped(item), on) is { } named && named.Symbol.Kind is SymbolKind.Label)
                yield return named;
            else
                yield break;
        }
    }

    /// <summary>
    /// The values of the data a label stands on, or none when it stands on no data. Only a
    /// table of addresses can name labels, so anything else spreads to nothing.
    /// </summary>
    private static IReadOnlyList<SyntaxNode> ItemsOfTable(Symbol target) =>
        target is { Kind: SymbolKind.Label, Data: { Kind: SyntaxKind.DataDirective } data }
            && data.ChildTokens.Length > 0
            && data.ChildTokens[0].Text.Equals(".addr", StringComparison.OrdinalIgnoreCase)
            ? data.ChildNodes
            : [];

    /// <summary>
    /// A table item with the <c>- 1</c> of an RTS dispatch table taken off it, which is the
    /// same label either way.
    /// </summary>
    private static SyntaxNode Stripped(SyntaxNode item) =>
        item is { Kind: SyntaxKind.BinaryExpression } && item.ChildNodes.Length == 2
            && item.ChildTokens.Any(token => token.Kind == SyntaxKind.Minus)
            ? item.ChildNodes[0]
            : item;

    /// <summary>
    /// A label nothing runs into and nothing names. Recognition is complete for what is
    /// written, so once the label exists the checks cover it; this is what pushes the
    /// programmer to write it down.
    /// </summary>
    private void CheckUnreachableLabels(FlowRegion region, List<Diagnostic> diagnostics)
    {
        if (!region.IsEntered)
            return;
        foreach (var block in region.Blocks)
        {
            if (block.Index == 0 || block.Label is not { } label || block.Predecessors.Count > 0)
                continue;
            if (model.ReferencesTo(label).Any(reference => !reference.IsDeclaration))
                continue;
            diagnostics.Add(new Diagnostic(label.DeclarationSpan, Severity.Warning,
                $"`{label.DisplayName}` is never reached: nothing runs into it and nothing names it"));
        }
    }

    /// <summary>
    /// Data the instruction above runs into, which is the <c>.byte $2c</c> skip and the
    /// opcodes ca65 has not got. A <c>.next</c> on the data says where flow goes instead of
    /// through it.
    /// </summary>
    private void CheckDataReachedByFallingThrough(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        var fromCode = false;
        foreach (var unit in units)
        {
            if (unit.Step.Label is not null)
                continue;
            var data = unit.Step.Statement.Kind == SyntaxKind.DataDirective;
            if (data && fromCode && unit.Next is null)
            {
                diagnostics.Add(new Diagnostic(
                    unit.Step.Statement.Tree.GetSpan(unit.Step.Statement.Span), Severity.Warning,
                    "the instruction above runs into this data. `.next` on it says where flow goes instead"));
            }

            // A run of data is one run: only what code runs into is worth saying.
            fromCode = !data && RunsOn(unit);
        }
    }

    /// <summary>Whether a statement is the last of its block, because control leaves after it.</summary>
    private bool EndsBlock(Unit unit) =>
        unit.Next is not null
        || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
            is not (Transfer.Through or Transfer.Call);

    /// <summary>
    /// Whether control carries on into whatever follows. A call does however it is written,
    /// because it comes back; a <c>.next</c> on anything else says where flow goes, and past
    /// the statement is not it.
    /// </summary>
    private bool RunsOn(Unit unit)
    {
        var transfer = Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode);
        if (transfer is Transfer.Call or Transfer.Elsewhere && IsCall(unit.Step.Statement))
            return true;
        return unit.Next is null && transfer is Transfer.Through or Transfer.Branch or Transfer.Call;
    }

    private static bool IsCall(SyntaxNode statement) =>
        statement.ChildTokens.Length > 0
        && (statement.ChildTokens[0].Text.Equals("jsr", StringComparison.OrdinalIgnoreCase)
            || statement.ChildTokens[0].Text.Equals("jsl", StringComparison.OrdinalIgnoreCase));

    /// <summary>One statement and the annotations written under it.</summary>
    private sealed class Unit(Step step)
    {
        /// <summary>The statement itself.</summary>
        public Step Step { get; } = step;

        /// <summary>The annotations under it, in the order they were written.</summary>
        public List<SyntaxNode> Annotations { get; } = [];

        /// <summary>The <c>.next</c> among them, or null when there is none.</summary>
        public SyntaxNode? Next =>
            Annotations.FirstOrDefault(a => a.Kind == SyntaxKind.NextDirective);
    }
}
