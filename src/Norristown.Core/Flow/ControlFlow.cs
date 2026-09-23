using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Where control goes inside each routine: its basic blocks and the edges between them,
/// built from the order layout wrote the bytes in, so the macros are expanded and the
/// repetitions unrolled before anything is asked about the path.
/// <para>
/// Every assembly-language trick is allowed, and each can be recognised from its syntax. Where
/// the operand does not say where flow goes — an indirect jump, a computed target — the edge
/// comes from a <c>.next</c> instead, and where there is none the path simply ends. On the
/// 6502 and its CMOS variants nothing consumes processor state, so no annotation is required.
/// </para>
/// </summary>
public sealed class ControlFlow
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly List<FlowRegion> regions = [];
    private readonly Dictionary<(int Position, Expansion? On), IReadOnlyList<StatementSyntax>> annotations = [];
    private readonly Dictionary<(int Position, Expansion? On), RelativeCall> relativeCalls = [];
    private readonly HashSet<(int Position, Expansion? On)> returnAddresses = [];
    private readonly List<RunningOn> runningOn = [];

    // Whether the file places another module or may be placed itself. In such a file a
    // `.fallthrough` may name a routine that the file's own layout cannot show comes next, so
    // the claim is left for the translation unit to check.
    private readonly bool placing;

    private ControlFlow(SemanticModel model, CodeLayout layout)
    {
        this.model = model;
        this.layout = layout;
        placing = layout.PlacePoints.Count > 0
            || Placements.Declaration(model.Tree) is { } module && Placements.MarkerOf(module) != ModulePlacement.Alone;
    }

    /// <summary>Every routine in the file, one region each.</summary>
    public IReadOnlyList<FlowRegion> Regions => regions;

    /// <summary>What is wrong with the paths through this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Every <c>.fallthrough</c> saying that flow runs on into a routine this file's own layout
    /// cannot show is next: one in another module, or one past a <c>.place</c>. Whether it is
    /// next is a question about the translation unit, answered once every file in it is laid out.
    /// </summary>
    public IReadOnlyList<RunningOn> RunningOn => runningOn;

    /// <summary>
    /// What the registers hold at each statement of the file, or null before it has been worked
    /// out. It is a question about the program rather than about one file, because what a
    /// statement leaves in a register follows from what the routines above it call.
    /// </summary>
    public RegisterStates? Registers { get; internal set; }

    /// <summary>Works out where control goes in <paramref name="layout"/>'s file.</summary>
    public static ControlFlow Of(SemanticModel model, CodeLayout layout)
    {
        var flow = new ControlFlow(model, layout);
        var diagnostics = new List<Diagnostic>();
        var inline = Inline(model.Tree);

        // A routine's bytes are one stream unless a nested segment block takes some of them
        // somewhere else. Fall-through stays inside a stream, but a jump may go from one to
        // another, so all of a routine's streams are one graph, its own stream first.
        foreach (var run in layout.Steps.Where(step => step.Routine is not null).GroupBy(step => step.Routine))
        {
            var routine = run.Key!;
            var units = flow.Units([.. run.GroupBy(step => step.Stream).SelectMany(stream => stream)]);
            flow.FindRelativeCalls(units);
            var blocks = flow.Blocks(units);

            // The routine is entered at the label of its own name.
            var entered = blocks.Count > 0 && blocks[0].Label == routine;
            CountedLoops.Find(model, layout, blocks);

            // A routine containing a line that layout could not lay out gets no count: the line
            // is missing from the stream, so counting the rest would pass off part as the whole.
            var (least, most, ends) = layout.Unlaid.Contains(routine)
                ? (null, null, true)
                : Paths.Through(blocks);
            var region = new FlowRegion(
                routine, entered, blocks, new RoutineCost(least, most, Calls(blocks), ends, Uncounted(blocks)),
                flow.Costed(blocks, inline), inline);
            flow.regions.Add(region);
            flow.CheckTargets(units, diagnostics);
            flow.CheckUnreachableLabels(region, diagnostics);
            flow.CheckDataReachedByFallingThrough(units, flow.CheckInlineData(units, diagnostics), diagnostics);
            flow.CheckNextIsNeeded(units, diagnostics);
            flow.CheckFallthrough(units, diagnostics);
            flow.CheckReturnsAndCalls(routine, units, diagnostics);
        }

        // On the 65816 the processor-state analysis depends on flow it cannot see for itself,
        // so each construct that hides some flow has to declare what it hides.
        if (layout.Cpu == Cpu.Wdc65816)
            Requirements.Check(model, layout, flow, diagnostics);
        else
            Requirements.CheckEnds(model, layout, flow, diagnostics);

        flow.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
        return flow;
    }

    /// <summary>
    /// Every <c>.scope</c> block written inside a routine, as the span of the line that opens
    /// it and of the whole block. One at file level is a namespace holding declarations, and
    /// holds no code of its own, so it is not one of these.
    /// </summary>
    private static List<(TextSpan Opener, TextSpan Whole)> Inline(SyntaxTree tree)
    {
        var found = new List<(TextSpan, TextSpan)>();
        Walk(tree.Root, false);
        return found;

        void Walk(SyntaxNode node, bool inProc)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is not BlockSyntax block)
                    continue;
                if (inProc && block.BlockKind == BlockKind.Scope)
                    found.Add((block.Opener.Span, block.FullSpan));
                Walk(block, inProc || block.BlockKind == BlockKind.Proc);
            }
        }
    }

    /// <summary>
    /// What each inline <c>.scope</c> of a routine costs. A scope whose lines are blocks of
    /// their own costs what a pass through those blocks costs; one written inside a single
    /// block costs what its own statements add up to, since a block runs all of it. A scope
    /// that is neither, whose first block nothing reaches, or that something branches into
    /// past its start, gets no cost: there is no single pass through it to count.
    /// </summary>
    private List<ScopeCost> Costed(
        IReadOnlyList<BasicBlock> blocks, IReadOnlyList<(TextSpan Opener, TextSpan Whole)> inline)
    {
        var costs = new List<ScopeCost>();
        foreach (var (opener, whole) in inline)
        {
            if (Cost(blocks, whole) is { } cost)
                costs.Add(new ScopeCost(opener, cost));
        }
        return costs;
    }

    /// <summary>What one pass through the part of a routine written inside <paramref name="whole"/> costs.</summary>
    private RoutineCost? Cost(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        var held = Held(blocks, whole);
        var part = 0;
        var all = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (held[i].Inside == 0)
                continue;
            if (held[i].Inside == held[i].Total)
                all++;
            else
                part++;
        }

        // Written inside one block, and so with nothing in it that branches: what it costs is
        // what its statements cost, and the block runs every one of them.
        if (part == 1 && all == 0)
        {
            var at = Array.FindIndex(held, block => block.Inside > 0);
            return blocks[at].IsReached ? Straight(blocks[at], whole) : null;
        }
        if (part > 0 || all == 0)
            return null;

        var inside = new bool[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
            inside[i] = held[i].Inside > 0;
        var entry = Array.FindIndex(inside, held => held);
        if (!blocks[entry].IsReached || inside.All(held => held))
            return null;

        // A branch into the middle of it means entering at the top is not the only way through,
        // so there is no single cost to give.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (inside[i] && i != entry && blocks[i].Predecessors.Any(from => !inside[from]))
                return null;
        }
        var (least, most, ends) = Paths.Through(blocks, entry, at => inside[at], Paths.Costing);
        return least is null ? null : new RoutineCost(least, most, Calls(blocks, inside), ends);
    }

    /// <summary>
    /// How many of each block's statements are written inside <paramref name="whole"/>, and how
    /// many it has. A statement written in this file decides by its position whether the walk
    /// is inside the span; a statement from an expansion counts as being wherever the call that
    /// expanded it was, so the walk carries the last answer forward over it.
    /// </summary>
    internal static (int Inside, int Total)[] Held(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        var held = new (int Inside, int Total)[blocks.Count];
        var within = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            var inside = 0;
            foreach (var step in blocks[i].Steps)
            {
                if (step.On is null)
                    within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
                if (within)
                    inside++;
            }
            held[i] = (inside, blocks[i].Steps.Count);
        }
        return held;
    }

    /// <summary>What the statements of <paramref name="block"/> written inside a span add up to.</summary>
    private RoutineCost? Straight(BasicBlock block, TextSpan whole)
    {
        var total = new CycleCount(0);
        var within = false;
        var any = false;
        foreach (var step in block.Steps)
        {
            if (step.On is null)
                within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
            if (!within || TakesNoTime(step))
                continue;
            if (layout.Of(step.Statement, step.On)?.Cycles is not { } cycles)
                return null;
            total += cycles;
            any = true;
        }
        return any ? new RoutineCost(total.Least, total.Most, false, true) : null;
    }

    /// <summary>Whether any block of a part of a routine calls.</summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks, bool[] inside)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (inside[i] && (blocks[i].Calls.Count > 0 || blocks[i].CallsUnknown))
                return true;
        }
        return false;
    }

    /// <summary>The annotations written under <paramref name="step"/>'s statement, in order.</summary>
    internal IReadOnlyList<StatementSyntax> AnnotationsOf(Step step) =>
        annotations.GetValueOrDefault((step.Statement.Position, step.On)) ?? [];

    /// <summary>Whether a statement calls a routine that never returns, which is where its path ends.</summary>
    internal bool CallsWhatNeverReturns(Step step)
    {
        if (RelativeCallAt(step) is { } relative)
            return relative.Routine.Signature is { NeverReturns: true };
        var mode = layout.Of(step.Statement, step.On)?.Mode;
        return Transfers.Of(step.Statement, mode) == Transfer.Call
            && Targets.Of(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Symbol.Signature is { NeverReturns: true };
    }

    /// <summary>
    /// Whether a routine calls anything, which its own cycle count does not follow into: here
    /// a call counts only the call instruction, and what the called routine takes is that
    /// routine's own count.
    /// </summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks) =>
        blocks.Any(block => block.Calls.Count > 0 || block.CallsUnknown);

    /// <summary>The call a branch makes, for a branch written as a relative call; null for every other statement.</summary>
    internal RelativeCall? RelativeCallAt(Step step) =>
        relativeCalls.TryGetValue((step.Statement.Position, step.On), out var call) ? call : null;

    /// <summary>Whether a statement is the <c>per</c> that pushes a relative call's return address.</summary>
    internal bool IsReturnAddress(Step step) => returnAddresses.Contains((step.Statement.Position, step.On));

    /// <summary>
    /// The statements of one region, each with the annotations written under it. An
    /// annotation is about the statement above it, so it belongs to that statement rather
    /// than standing on its own.
    /// </summary>
    private List<Unit> Units(IReadOnlyList<Step> steps)
    {
        var units = new List<Unit>();
        foreach (var step in steps)
        {
            // An annotation after a macro call is about the last statement of its expansion,
            // which is before the step that marks where the expansion ends.
            if (step.Statement is StatementSyntax annotation && Annotations.Is(annotation)
                && units.FindLast(unit => !unit.Step.IsMarker) is { } above)
            {
                above.Annotations.Add(annotation);
                continue;
            }
            units.Add(new Unit(step));
        }
        foreach (var unit in units.Where(unit => unit.Annotations.Count > 0))
            annotations[(unit.Step.Statement.Position, unit.Step.On)] = unit.Annotations;
        return units;
    }

    /// <summary>
    /// The branches that are calls: <c>per L-1</c> directly before <c>brl f</c> or
    /// <c>bra f</c> to a routine, with <c>L</c> the label directly after the branch, and a
    /// <c>phk</c> directly before the <c>per</c> for a far one.
    /// </summary>
    private void FindRelativeCalls(IReadOnlyList<Unit> units)
    {
        for (var i = 1; i + 1 < units.Count; i++)
        {
            var branch = units[i].Step;
            var push = units[i - 1];
            if (push.Step.Stream != branch.Stream || units[i + 1].Step.Stream != branch.Stream
                || !(IsInstruction(branch.Statement, "brl") || IsInstruction(branch.Statement, "bra"))
                || !IsInstruction(push.Step.Statement, "per") || push.Next is not null || units[i].Next is not null
                || units[i + 1].Step.Label is not { } after
                || Targets.Of(model, Transfers.TargetOf(branch.Statement, AddressingMode.Relative), branch.On)
                    is not { Symbol.Signature: not null } routine
                || !NamesTheAddressBefore(push.Step, after))
            {
                continue;
            }
            var far = i >= 2 && units[i - 2].Step.Stream == branch.Stream
                && IsInstruction(units[i - 2].Step.Statement, "phk");
            relativeCalls[(branch.Statement.Position, branch.On)] = new RelativeCall(routine.Symbol, far);
            returnAddresses.Add((push.Step.Statement.Position, push.Step.On));
        }
    }

    /// <summary>Whether a <c>per</c> pushes <c>L-1</c>, the byte before <paramref name="label"/>, as a return address is.</summary>
    private bool NamesTheAddressBefore(Step push, Symbol label)
    {
        if (push.Statement is not InstructionStatementSyntax { Operand: AbsoluteOperandSyntax { Prefix: null } pushed }
            || pushed.Address is not BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference)
        {
            return false;
        }
        return Targets.Of(model, difference.Left, push.On)?.Symbol == label
            && model.ValueOf(difference.Right, push.On).AsNumber() == 1;
    }

    private static bool IsInstruction(SyntaxNode statement, string mnemonic) =>
        statement is InstructionStatementSyntax instruction
        && instruction.Mnemonic.Text.Equals(mnemonic, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The blocks of one region. A label starts a block, and a statement that transfers
    /// control ends one, so if any of a block runs, all of it runs.
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

        int? stream = null;
        foreach (var unit in units)
        {
            // Nothing runs from the end of one stream into the start of the next.
            if (unit.Step.Stream != stream)
            {
                open = false;
                runsOn = false;
                stream = unit.Step.Stream;
            }
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
        {
            blocks[i].IsFallenInto = fallenInto[i];
            blocks[i].Next = tails[i]?.Next;
            blocks[i].RunsInto = tails[i]?.Step is { Statement: FallthroughDirectiveSyntax { Target: { } into } } end
                ? RoutineNamed(into, end.On)
                : null;
            blocks[i].Cycles = Counted(blocks[i]);
        }
        return blocks;

        void Open(Symbol? label, Expansion? on)
        {
            blocks.Add(new BasicBlock(blocks.Count, label, on, stream!.Value));
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

            var mode = layout.Of(tail.Step.Statement, tail.Step.On)?.Mode;
            var transfer = Transfers.Of(tail.Step.Statement, mode);
            var relative = RelativeCallAt(tail.Step);
            var calls = transfer == Transfer.Call || relative is not null;

            // A `.next` replaces the operand as the source of the targets, because the operand
            // does not identify them. On a call it lists the routines called, and the call
            // still returns to the statement after it.
            if (tail.Next is { } next)
            {
                foreach (var named in Named(next, tail.Step.On))
                {
                    if (found.TryGetValue(named, out var to))
                        Edge(i, to, EdgeKind.Declared);
                    if (calls)
                        blocks[i].Called(named.Symbol);
                }
                continue;
            }

            if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call))
            {
                // A `jsr` whose target the operand does not name is a call all the same, and
                // one nothing here can follow into.
                blocks[i].CallsUnknown |= transfer == Transfer.Elsewhere && IsCall(tail.Step.Statement);
                continue;
            }
            var target = Targets.Of(model, Transfers.TargetOf(tail.Step.Statement, mode), tail.Step.On);
            var inside = target is { } named2 && found.TryGetValue(named2, out var reached);
            if (inside)
                Edge(i, found[target!.Value], calls ? EdgeKind.Call : EdgeKind.Taken);

            // What a call reaches costs what that routine costs, and so does what a tail jump
            // reaches, since control comes back from it to this routine's caller. A target
            // this routine holds itself is neither: the path simply carries on into it.
            if (relative is { } known)
                blocks[i].Called(known.Routine);
            else if (calls || (transfer == Transfer.Jump && !inside))
                blocks[i].SetCalled(target?.Symbol);
        }

        void Edge(int from, int to, EdgeKind kind)
        {
            blocks[from].Reach(to, kind);
            blocks[to].ReachedFrom(from);
        }
    }

    /// <summary>
    /// How long the whole block takes. A block runs all of it or none, so the counts add up;
    /// one statement nt65 has no count for leaves the block without one.
    /// </summary>
    private CycleCount? Counted(BasicBlock block)
    {
        var total = new CycleCount(0);
        foreach (var step in block.Steps)
        {
            if (TakesNoTime(step))
                continue;
            if (layout.Of(step.Statement, step.On)?.Cycles is not { } cycles)
            {
                block.Uncounted = Uncounted(step);
                return null;
            }
            total += cycles;
        }

        // A block with nothing in it is the one a routine opens with when its first line is a
        // label, and running none of it takes no time at all.
        return total;
    }

    /// <summary>
    /// Whether a step takes no time because it generates nothing that runs: a <c>.state</c>,
    /// a <c>.frame</c>, a <c>.fallthrough</c>, or a marker for either end of an expansion.
    /// </summary>
    private static bool TakesNoTime(Step step) =>
        step.Statement is StateDirectiveSyntax or FrameDirectiveSyntax or FallthroughDirectiveSyntax || step.IsMarker;

    /// <summary>
    /// Why a statement nt65 recognises has no count, for the editor's code lens, which would
    /// otherwise show the routine without a count and give no reason; null for a line nt65
    /// could not lay out, which has already been reported where it is written.
    /// </summary>
    private static string? Uncounted(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.Mnemonic.Text.ToLowerInvariant() switch
        {
            "mvn" or "mvp" => "a block move takes 7 cycles a byte, and how many is in A",
            "jam" => "`jam` stops the processor, and nothing after it runs until a reset",
            _ => null,
        };

    /// <summary>Why a routine has no count: the first block a path reaches that has none says so.</summary>
    private static string? Uncounted(IReadOnlyList<BasicBlock> blocks) =>
        blocks.FirstOrDefault(block => block.IsReached && block.Uncounted is not null)?.Uncounted;

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
    /// items are all code labels or routines — each optionally minus one, as an RTS dispatch
    /// table writes them — stands for every one of those labels, which is how an indirect
    /// call names the routines in its table.
    /// </summary>
    internal IEnumerable<(Symbol Symbol, Expansion? At)> Named(NextDirectiveSyntax next, Expansion? on)
    {
        foreach (var written in next.Targets)
        {
            // A `list` parameter names every label the call gave it.
            if (model.SymbolOf(written, on) is { Kind: SymbolKind.MacroParameter, Parameter.Kind: ParameterKind.List } list
                && model.GivenAt(list, on) is { Argument: var given, Caller: var caller })
            {
                foreach (var item in given.Items)
                {
                    if (Targets.Of(model, item, caller) is { } each)
                        yield return each;
                }
                continue;
            }

            if (Targets.Of(model, written, on) is not { } target)
                continue;
            var spread = Spread(target.Symbol, on).ToList();
            if (spread.Count > 0)
            {
                foreach (var item in spread)
                    yield return item;
                continue;
            }

            // Data that is not a table of code labels names nowhere code goes, which is
            // reported where the targets are checked rather than followed into the bytes.
            if (IsDataWithoutCodeLabels(target.Symbol, on))
                continue;
            yield return target;
        }
    }

    /// <summary>
    /// The labels a table or a list expands to; empty when it does not expand to labels and
    /// so names only itself.
    /// </summary>
    private IEnumerable<(Symbol Symbol, Expansion? At)> Spread(Symbol target, Expansion? on)
    {
        var items = target.Kind == SymbolKind.List
            ? target.Items.Select(item => (Item: item, On: on))
            : ItemsOfTable(target);
        foreach (var (item, at) in items)
        {
            if (Targets.Of(model, Stripped(item), at) is { } named
                && (named.Symbol.Kind is SymbolKind.Label || named.Symbol.Signature is not null))
            {
                yield return named;
            }
            else
                yield break;
        }
    }

    /// <summary>
    /// The values of a table, which is data declared as addresses with <c>.addr</c> or
    /// <c>.faraddr</c>, each paired with the writing it is on. Values in a body are read from
    /// the writings layout made of them, since a repetition there may write each one
    /// differently. Any other symbol yields no values.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsOfTable(Symbol target)
    {
        if (!IsAddressData(target) || target.Data is not DataDirectiveSyntax element)
            yield break;
        foreach (var value in DataLengths.ElementsOf(element))
            yield return (value, null);
        if (DataSyntax.BodyOf(element) is null)
            yield break;
        foreach (var step in layout.Steps)
        {
            if (step.Statement is DataValuesSyntax values && DataSyntax.DirectiveOfValues(values) == element)
            {
                foreach (var value in DataLengths.ElementsOf(values))
                    yield return (value, step.On);
            }
        }
    }

    /// <summary>Whether a symbol is data declared as addresses, which is what a table of targets is.</summary>
    private static bool IsAddressData(Symbol symbol) =>
        symbol is { Kind: SymbolKind.Data, Data: DataDirectiveSyntax element } && DataSyntax.NameOf(element) is ".addr" or ".faraddr";

    /// <summary>Whether a name is data that does not spread to code labels, which names nowhere code goes.</summary>
    private bool IsDataWithoutCodeLabels(Symbol symbol, Expansion? on) =>
        symbol.Kind == SymbolKind.Data && !Spread(symbol, on).Any();

    /// <summary>
    /// A table item with the <c>- 1</c> of an RTS dispatch table taken off it, which is the
    /// same label either way.
    /// </summary>
    private static SyntaxNode Stripped(SyntaxNode item) =>
        item is BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference ? difference.Left : item;

    /// <summary>
    /// What an annotation names has to be somewhere code can be: a label, a routine, or a
    /// list or a table of them. Which kind a name is settles only once every symbol has a
    /// value, which is why this is checked here rather than where the name was resolved.
    /// </summary>
    private void CheckTargets(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        foreach (var annotation in units.SelectMany(unit => unit.Annotations.Select(a => (unit.Step.On, a))))
        {
            foreach (var written in Annotations.TargetsOf(annotation.a))
            {
                if (Targets.Of(model, written, annotation.On) is not { } target)
                    continue;
                if (IsDataWithoutCodeLabels(target.Symbol, annotation.On))
                {
                    diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span),
                        IsAddressData(target.Symbol)
                            ? Catalogue.NextTableHasNoLabels.Says(target.Symbol.DisplayName)
                            : Catalogue.NextTargetNotATable.Says(target.Symbol.DisplayName)));
                    continue;
                }
                if (target.Symbol.IsAddress || target.Symbol.Kind == SymbolKind.List)
                    continue;
                diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span),
                    Catalogue.NextTargetNotCode.Says(
    target.Symbol.DisplayName, target.Symbol.KindPhrase, Annotations.Spell(annotation.a))));
            }
        }
    }

    /// <summary>
    /// Reports a label that nothing falls into and nothing names. nt65 sees every reference
    /// written in the source, so such a label can only be reached in a way nt65 cannot see,
    /// and this diagnostic pushes the programmer to write down how. A <c>.state</c> directly
    /// after the label declares it an entry point, which acknowledges that it is reached from
    /// somewhere nt65 cannot see. A label on data is read rather than run, so control never
    /// reaching it is expected.
    /// </summary>
    private void CheckUnreachableLabels(FlowRegion region, List<Diagnostic> diagnostics)
    {
        if (!region.IsEntered)
            return;
        foreach (var block in region.Blocks)
        {
            // Code that opens a nested segment block with no label is somewhere fall-through
            // never goes, and nothing can name it either.
            if (block.Index > 0 && block.Label is null && block.Predecessors.Count == 0
                && block.Stream != region.Blocks[block.Index - 1].Stream
                && block.Steps is [{ Statement: InstructionStatementSyntax } first, ..])
            {
                diagnostics.Add(new Diagnostic(first.Statement.Tree.GetSpan(first.Statement.Span),
                    Catalogue.CodeUnreachable));
                continue;
            }
            if (block.Index == 0 || block.Label is not { Kind: not SymbolKind.Data } label || block.Predecessors.Count > 0
                || block.IsDeclared || block.Steps is [{ Statement: DataDirectiveSyntax or DataValuesSyntax }, ..])
            {
                continue;
            }
            if (model.ReferencesTo(label).Any(reference => !reference.IsDeclaration))
                continue;
            diagnostics.Add(new Diagnostic(label.DeclarationSpan,
                Catalogue.LabelUnreachable.Says(label.DisplayName)));
        }
    }

    /// <summary>
    /// Whether anything other than its declaration refers to <paramref name="symbol"/>.
    /// </summary>
    private bool IsNamed(Symbol symbol) =>
        model.ReferencesTo(symbol).Any(reference => !reference.IsDeclaration);

    /// <summary>
    /// Reports data that the instruction above falls through into, as happens with the
    /// <c>.byte $2c</c> skip trick and with opcodes ca65 lacks written out as bytes. A
    /// <c>.next</c> on the data says where flow goes instead of through it.
    /// </summary>
    private void CheckDataReachedByFallingThrough(
        IReadOnlyList<Unit> units, HashSet<Unit> inline, List<Diagnostic> diagnostics)
    {
        // On the 65816 flow that runs into data reaches the analysis, which cannot follow it,
        // so there the annotation is required rather than suggested.
        var severity = layout.Cpu == Cpu.Wdc65816 ? Severity.Error : Severity.Warning;
        var fromCode = false;
        int? stream = null;
        Unit? before = null;
        foreach (var unit in units)
        {
            if (unit.Step.Stream != stream)
            {
                fromCode = false;
                stream = unit.Step.Stream;
            }
            if (unit.Step.Label is not null || unit.Step.IsMarker)
                continue;
            var data = unit.Step.Statement is DataDirectiveSyntax or DataValuesSyntax;

            // The data a routine returns past is skipped, and flow carries on after it.
            if (inline.Contains(unit))
            {
                fromCode = true;
                continue;
            }
            if (data && fromCode && unit.Next is null)
            {
                diagnostics.Add(new Diagnostic(
                    unit.Step.Statement.Tree.GetSpan(unit.Step.Statement.Span), severity,
                    Catalogue.RunsIntoData)
                {
                    Fix = AlwaysTaken(before),
                });
            }

            // Consecutive data lines are reported once: only the first, which code runs into.
            fromCode = !data && RunsOn(unit);
            before = unit;
        }
    }

    /// <summary>
    /// The <c>.next</c> that says a conditional branch running into data is always taken, which
    /// is what data after one nearly always means: the flags are known there, and the bytes
    /// after it are text or a table the branch jumps over. Offered where the branch is written in
    /// this file, and named as it is written, so the edit reads as the programmer would write it.
    /// </summary>
    private DiagnosticFix? AlwaysTaken(Unit? branch)
    {
        if (branch is not { Step: { On: null, Statement: InstructionStatementSyntax statement } } || statement.Tree != model.Tree
            || BranchTarget(branch) is null
            || Transfers.TargetOf(statement, layout.Of(statement, null)?.Mode) is not { } written)
        {
            return null;
        }
        return new DiagnosticFix(FixKind.AlwaysTaken, written.GetText().Trim(), statement.Tree.GetSpan(statement.Span));
    }

    /// <summary>
    /// The data after each call to a routine that returns past it, which has to be what the
    /// routine's <c>inline</c> item says: <c>n</c> bytes of data, or one <c>.strz</c>. This
    /// holds on every CPU, because the call returns past it whatever the processor.
    /// </summary>
    private HashSet<Unit> CheckInlineData(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        var skipped = new HashSet<Unit>();
        for (var i = 0; i < units.Count; i++)
        {
            if (CalledAt(units[i]) is not { Signature.Inline: { } inline } routine)
                continue;
            var call = units[i].Step.Statement;
            var name = routine.DisplayName;

            if (inline.IsStrz)
            {
                if (i + 1 < units.Count && units[i + 1].Step.Label is null
                    && units[i + 1].Step.Stream == units[i].Step.Stream
                    && units[i + 1].Step.Statement is DataDirectiveSyntax text
                    && text.Directive.Text.Equals(".strz", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(units[i + 1]);
                }
                else
                {
                    Report(call, Catalogue.InlineDataMissing.Says(name, "one `.strz`", "none follows this one"));
                }
                continue;
            }

            if (inline.Expression is not { } count
                || model.ValueOf(count, units[i].Step.On).AsNumber() is not { } bytes || bytes < 0)
            {
                Report(call, Catalogue.InlineCountNotConstant.Says(name, inline.Text));
                continue;
            }

            // The data is the run of it directly after the call, as long as it takes to make up
            // the count.
            var taken = 0L;
            for (var j = i + 1; j < units.Count && taken < bytes; j++)
            {
                if (units[j].Step.Label is not null || units[j].Step.Stream != units[i].Step.Stream
                    || units[j].Step.Statement is not (DataDirectiveSyntax or DataValuesSyntax))
                    break;
                taken += layout.Of(units[j].Step.Statement, units[j].Step.On)?.Length ?? 0;
                skipped.Add(units[j]);
            }
            if (taken != bytes)
            {
                Report(call, Catalogue.InlineDataMissing.Says(
                    name,
                    $"{Bytes(bytes)} of data",
                    taken == 0 ? "none follows this one" : $"{Bytes(taken)} {(taken == 1 ? "follows" : "follow")} this one"));
            }
        }
        return skipped;

        void Report(SyntaxNode node, DiagnosticMessage message) =>
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));

        static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";
    }

    /// <summary>
    /// How a routine that never returns and an interrupt handler may be left and reached, which
    /// holds on every CPU: neither returns with <c>rts</c> or <c>rtl</c>, and an interrupt handler,
    /// which leaves by <c>rti</c>, is never called.
    /// </summary>
    private void CheckReturnsAndCalls(Symbol routine, IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        foreach (var unit in units)
        {
            var statement = unit.Step.Statement;
            if (unit.Next is null && routine.Signature is { HasNoCaller: true } own
                && statement is InstructionStatementSyntax instruction
                && (IsInstruction(instruction, "rts") || IsInstruction(instruction, "rtl")))
            {
                var returned = instruction.Mnemonic.Text.ToLowerInvariant();
                Report(statement, own.IsInterrupt
                    ? Catalogue.HandlerReturnsNotRti.Says(routine.DisplayName, returned)
                    : Catalogue.NoreturnReturns.Says(routine.DisplayName, returned),

                    // A handler is left by `rti`, which is the instruction to write instead. A
                    // routine that never returns has no instruction that would do: what it
                    // should do there is leave some other way, or not say `noreturn`.
                    own.IsInterrupt ? new DiagnosticFix(FixKind.Return, "rti") : null);
            }
            if (CalledAt(unit) is { Signature.IsInterrupt: true } handler)
            {
                Report(statement, Catalogue.HandlerCalled.Says(handler.DisplayName));
            }
        }

        void Report(SyntaxNode node, DiagnosticMessage message, DiagnosticFix? fix = null) =>
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message) { Fix = fix });
    }

    /// <summary>The routine a statement calls, directly or as a relative call; null for anything else.</summary>
    private Symbol? CalledAt(Unit unit)
    {
        if (RelativeCallAt(unit.Step) is { } relative)
            return relative.Routine;
        var statement = unit.Step.Statement;
        var mode = layout.Of(statement, unit.Step.On)?.Mode;
        return Transfers.Of(statement, mode) == Transfer.Call
            ? Targets.Of(model, Transfers.TargetOf(statement, mode), unit.Step.On)?.Symbol
            : null;
    }

    /// <summary>The routine a <c>.fallthrough</c> names, or null where it names something else or nothing.</summary>
    private Symbol? RoutineNamed(NameExpressionSyntax written, Expansion? on) =>
        Targets.Of(model, written, on)?.Symbol is { Kind: SymbolKind.Proc, Signature: not null } routine ? routine : null;

    /// <summary>
    /// A <c>.next</c> names where flow goes after a statement nt65 cannot follow: an indirect
    /// jump or call, a return, a jump to a computed address, data flow runs into, and the last
    /// statement of a nested segment block, which runs into whatever that segment holds next.
    /// Under a conditional branch it names the branch's own target and nothing else, which says
    /// the branch is always taken. After any other statement nt65 already knows where flow goes,
    /// and the <c>.next</c> could only contradict it. <c>.next ?</c> ends a path wherever it stands.
    /// </summary>
    private void CheckNextIsNeeded(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        if (units.Count == 0)
            return;
        var own = units[0].Step.Stream;
        var endsASegmentBlock = units
            .Where(unit => unit.Step.Stream != own && !unit.Step.IsMarker && unit.Step.Label is null)
            .GroupBy(unit => unit.Step.Stream)
            .Select(stream => stream.Last())
            .ToHashSet();
        foreach (var unit in units)
        {
            if (unit.Next is not { QuestionToken: null } next || endsASegmentBlock.Contains(unit))
                continue;

            // A branch that is always taken goes only to its own target.
            if (BranchTarget(unit) is { } target)
            {
                if (Named(next, unit.Step.On).Select(named => named.Symbol).ToList() is [var only] && only == target
                    && next.Targets.Count == 1)
                {
                    continue;
                }
                diagnostics.Add(new Diagnostic(next.Tree.GetSpan(next.Keyword.Span), Severity.Error,
                    Catalogue.NextNotTheBranchTarget.Says($"`{unit.Step.Statement.GetText().Trim()}`", target.DisplayName)));
                continue;
            }
            if (Known(unit) is not { } does)
                continue;

            // Where the `.next` ends a routine's body, what was meant is nearly always that the
            // routine runs into the one after it, which is what `.fallthrough` says.
            var ends = next.Parent is LineSyntax line && Fallthrough.EndsABody(line);
            var rewrite = ends && next.Tree == model.Tree && next.Targets.Count == 1
                && RoutineNamed(next.Targets[0], null) is not null;
            diagnostics.Add(new Diagnostic(next.Tree.GetSpan(next.Keyword.Span), Severity.Error,
                Catalogue.NextSuccessorsKnown.Says(
                    $"`{unit.Step.Statement.GetText().Trim()}`", does,
                    ends ? ": a routine that runs into the one after it says so with `.fallthrough`" : ""))
            {
                Fix = rewrite ? new DiagnosticFix(FixKind.Spelling, ".fallthrough") : null,
            });
        }
    }

    /// <summary>
    /// The label or routine a conditional branch goes to when it is taken, short or long, or null
    /// for any other statement and for a branch whose target nt65 cannot read, such as <c>*+3</c>.
    /// A relative call written with <c>per</c> and a branch is a call, not a branch.
    /// </summary>
    private Symbol? BranchTarget(Unit unit)
    {
        if (unit.Step.Statement is not InstructionStatementSyntax statement || RelativeCallAt(unit.Step) is not null)
            return null;
        var mode = layout.Of(statement, unit.Step.On)?.Mode;
        return Transfers.Of(statement, mode) == Transfer.Branch
            && Targets.Of(model, Transfers.TargetOf(statement, mode), unit.Step.On) is { Symbol.IsAddress: true } target
                ? target.Symbol
                : null;
    }

    /// <summary>
    /// Where flow goes after a statement, phrased for a diagnostic message, where nt65 can work
    /// it out for itself; null where it cannot, which is where a <c>.next</c> is needed.
    /// </summary>
    private string? Known(Unit unit)
    {
        if (unit.Step.Statement is not InstructionStatementSyntax statement)
            return null;
        if (RelativeCallAt(unit.Step) is { } relative)
            return $"calls `{relative.Routine.DisplayName}` and comes back";
        var mode = layout.Of(statement, unit.Step.On)?.Mode;
        var transfer = Transfers.Of(statement, mode);
        if (transfer == Transfer.Through)
            return "runs on into what follows it";
        if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call)
            || Targets.Of(model, Transfers.TargetOf(statement, mode), unit.Step.On) is not { Symbol.IsAddress: true } target)
        {
            return null;
        }
        var named = target.Symbol.DisplayName;
        return transfer switch
        {
            Transfer.Call => $"calls `{named}` and comes back",
            Transfer.Jump => $"goes to `{named}`",
            _ => $"goes to `{named}` or on into what follows it",
        };
    }

    /// <summary>
    /// A <c>.fallthrough</c> says that every path reaching the end of the routine runs into the
    /// routine it names, which is true only when that routine starts where this one ends, in the
    /// same segment. Where the file places another module or may be placed, the routine may be
    /// another module's, or past a <c>.place</c>, and whether it comes next can then only be
    /// decided for the whole translation unit, so the claim is recorded in
    /// <see cref="RunningOn"/> for that check.
    /// </summary>
    private void CheckFallthrough(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        foreach (var unit in units)
        {
            if (unit.Step is not { Statement: FallthroughDirectiveSyntax { Target: { } written } directive, On: null } step)
                continue;
            if (Targets.Of(model, written, null) is not { } target)
                continue;
            if (RoutineNamed(written, null) is not { } routine)
            {
                diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span),
                    Catalogue.FallthroughNotARoutine.Says(target.Symbol.DisplayName, target.Symbol.KindPhrase)));
                continue;
            }
            if (step.Segment is { } here && routine.Segment is { } there && here != there)
            {
                diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span),
                    Catalogue.FallthroughOtherSegment.Says(routine.DisplayName, here, there)));
                continue;
            }
            if (layout.Placed(directive) is { } end && routine.Tree == model.Tree && layout.Placed(routine) is { } start
                && start.Stream == end.Stream && start.Offset == end.End)
            {
                continue;
            }
            if (placing || routine.Tree != model.Tree)
            {
                runningOn.Add(new RunningOn(directive, null, routine, written));
                continue;
            }
            diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span),
                Catalogue.FallthroughNotAdjacent.Says(routine.DisplayName)));
        }
    }

    /// <summary>
    /// The routine written directly after <paramref name="region"/>'s, in the same run of its
    /// segment's bytes, whatever regions of other segments come between them in the text, and the
    /// <c>}</c> that closes this one's body, which is where a <c>.fallthrough</c> naming it goes;
    /// null where anything with bytes in that segment, or nothing at all, comes next.
    /// </summary>
    internal (Symbol Routine, Span Closer)? WrittenAfter(FlowRegion region)
    {
        var steps = layout.Steps;
        var opened = -1;
        var last = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Label == region.Routine && steps[i].Statement is ProcDeclarationSyntax)
                opened = i;
            if (opened >= 0 && steps[i].Routine == region.Routine && steps[i].Stream == steps[opened].Stream)
                last = i;
        }
        if (opened < 0 || steps[opened].Statement.Parent?.Parent is not BlockSyntax { Closer: { } closer })
            return null;
        var segment = steps[opened].Segment;
        var run = layout.Placed(region.Routine)?.Stream;
        for (var i = last + 1; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Segment != segment)
                continue;

            // An `.align` or a `.place` in between starts another run, and a routine in a
            // different run from this one's does not directly follow it.
            if (step.Label is { Kind: SymbolKind.Proc, Signature: not null } next && step.Statement is ProcDeclarationSyntax)
                return layout.Placed(next)?.Stream == run ? (next, closer.Tree.GetSpan(closer.Span)) : null;
            if (step.Label is not null || layout.Placed(step.Statement, step.On) is { Length: not 0 })
                return null;
        }
        return null;
    }

    /// <summary>
    /// Whether a statement is the last of its block, because control leaves after it. A call
    /// leaves too, even though it comes back: what it reaches is an edge of its own, and an
    /// edge leaves a block at its end.
    /// </summary>
    private bool EndsBlock(Unit unit) =>
        unit.Next is not null || unit.Step.Statement is FallthroughDirectiveSyntax
        || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
            != Transfer.Through;

    /// <summary>
    /// Whether control carries on into whatever follows. A call does, however it is written,
    /// because it returns, unless it calls a routine that never returns. On any other
    /// statement a <c>.next</c> says where flow goes, and that replaces carrying on past it.
    /// </summary>
    private bool RunsOn(Unit unit)
    {
        if (unit.Step.Statement is FallthroughDirectiveSyntax)
            return false;
        if (CalledAt(unit) is { Signature.NeverReturns: true })
            return false;
        var transfer = Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode);
        if (transfer is Transfer.Call or Transfer.Elsewhere && IsCall(unit.Step.Statement))
            return true;
        if (RelativeCallAt(unit.Step) is not null)
            return true;

        return unit.Next is null && transfer is Transfer.Through or Transfer.Branch or Transfer.Call;
    }

    private static bool IsCall(SyntaxNode statement) =>
        statement is InstructionStatementSyntax instruction && Instructions.Facts(instruction.Mnemonic.Text).Calls;

    /// <summary>One statement and the annotations written under it.</summary>
    private sealed class Unit(Step step)
    {
        /// <summary>The statement itself.</summary>
        public Step Step { get; } = step;

        /// <summary>The annotations under it, in the order they were written.</summary>
        public List<StatementSyntax> Annotations { get; } = [];

        /// <summary>The <c>.next</c> among them, or null when there is none.</summary>
        public NextDirectiveSyntax? Next => Annotations.OfType<NextDirectiveSyntax>().FirstOrDefault();
    }
}
