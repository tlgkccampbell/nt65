using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out where control goes inside each routine, as basic blocks and the edges between
/// them. The blocks are built from the order in which layout emitted the bytes, so the macros
/// are expanded and the repetitions unrolled before anything is asked about the path.
/// <para>
/// Every assembly-language trick is allowed, and each can be recognised from its syntax. Where
/// the operand does not say where flow goes, as with an indirect jump or a computed target, the
/// edge comes from a <c>.next</c> instead. Where there is no <c>.next</c>, the path simply ends. On the
/// 6502 and its CMOS variants nothing consumes processor state, so no annotation is required.
/// </para>
/// </summary>
public sealed class ControlFlow
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly List<FlowRegion> regions = [];
    private readonly Dictionary<StepKey, IReadOnlyList<StatementSyntax>> annotations;
    private readonly Dictionary<StepKey, RelativeCall> relativeCalls;
    private readonly HashSet<StepKey> returnAddresses;

    private ControlFlow(SemanticModel model, CodeLayout layout)
        : this(model, layout, [], [], [])
    {
    }

    private ControlFlow(
        SemanticModel model, CodeLayout layout,
        Dictionary<StepKey, IReadOnlyList<StatementSyntax>> annotations,
        Dictionary<StepKey, RelativeCall> relativeCalls,
        HashSet<StepKey> returnAddresses)
    {
        this.model = model;
        this.layout = layout;
        this.annotations = annotations;
        this.relativeCalls = relativeCalls;
        this.returnAddresses = returnAddresses;
    }

    /// <summary>Gets every routine in the file, one region each.</summary>
    public IReadOnlyList<FlowRegion> Regions => regions;

    /// <summary>Gets what is wrong with the paths through this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Gets every <c>.fallthrough</c> claiming that flow runs on into a routine that this file's own
    /// layout cannot show is next. Such a routine is in another module, or past a <c>.place</c>.
    /// Whether it is next is a question about the translation unit, answered once every file in it
    /// is laid out.
    /// </summary>
    public IReadOnlyList<RunningOn> RunningOn { get; private set; } = [];

    /// <summary>
    /// Gets what the registers hold at each statement of the file, or null before it has been
    /// worked out. It is a question about the program rather than about one file, because what a
    /// statement leaves in a register follows from what the routines above it call.
    /// </summary>
    public RegisterStates? Registers { get; internal set; }

    /// <summary>Works out where control goes in <paramref name="layout"/>'s file.</summary>
    public static ControlFlow Of(SemanticModel model, CodeLayout layout)
    {
        var flow = new ControlFlow(model, layout);
        var checks = new FlowChecks(model, layout, flow);
        var inline = Inline(model.Tree);

        // Every routine's blocks are built before any loop is counted, because a loop that calls
        // a routine in this file is counted only where that routine's body leaves the counter
        // alone.
        var built = new List<(Symbol Routine, List<Unit> Units, List<BasicBlock> Blocks, HashSet<Unit> InlineData)>();

        // A routine's bytes are one stream unless a nested segment block takes some of them
        // somewhere else. Fall-through stays inside a stream, but a jump may go from one to
        // another, so all of a routine's streams are one graph, its own stream first.
        foreach (var run in layout.Steps.Where(step => step.Routine is not null).GroupBy(step => step.Routine))
        {
            var routine = run.Key!;
            var units = flow.Units([.. run.GroupBy(step => step.Stream).SelectMany(stream => stream)]);

            // Where a block ends and what it costs depend on which branches are calls and on
            // which data a call returns past, so both are found first and handed to the blocks.
            var (calls, returnAddresses) = flow.FindRelativeCalls(units);
            var inlineData = checks.FindInlineData(units, calls);
            var blocks = flow.Blocks(units, calls, inlineData);
            foreach (var (at, call) in calls)
                flow.relativeCalls[at] = call;
            flow.returnAddresses.UnionWith(returnAddresses);
            built.Add((routine, units, blocks, inlineData));
        }

        var bodies = built.ToDictionary(each => each.Routine, each => (IReadOnlyList<BasicBlock>)each.Blocks);
        foreach (var (routine, units, blocks, inlineData) in built)
        {
            // The routine is entered at the label of its own name.
            var entered = blocks.Count > 0 && blocks[0].Label == routine;
            CountedLoops.Find(model, layout, blocks, bodies);

            // A routine containing a line that layout could not lay out gets no count. The line
            // is missing from the stream, so counting the rest would pass off part as the whole.
            var (minimum, maximum, ends) = layout.Unlaid.Contains(routine)
                ? (null, null, true)
                : Paths.Through(blocks);
            var region = new FlowRegion(
                routine, entered, blocks, new RoutineCost(minimum, maximum, Calls(blocks), ends, Uncounted(blocks)),
                flow.Costed(blocks, inline), inline);
            flow.regions.Add(region);
            checks.Check(region, units, inlineData);
        }
        flow.RunningOn = checks.RunningOn;

        // On the 65816 the processor-state analysis depends on flow it cannot see for itself,
        // so each construct that hides some flow has to declare what it hides.
        var diagnostics = new List<Diagnostic>(checks.Found);
        if (layout.Cpu == Cpu.Wdc65816)
            Requirements.Check(model, layout, flow, diagnostics);
        else
            Requirements.CheckEnds(model, layout, flow, diagnostics);

        flow.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
        return flow;
    }

    /// <summary>
    /// Returns whether a symbol is data declared as addresses, which is what a table of targets is.
    /// </summary>
    internal static bool IsAddressData(Symbol symbol) =>
        symbol is { Kind: SymbolKind.Data, Data: DataDirectiveSyntax element } && element.Directive.DirectiveKind is DirectiveKind.Addr or DirectiveKind.FarAddr;

    /// <summary>
    /// Returns whether a statement is a call instruction, whether or not its operand names the
    /// routine it calls.
    /// </summary>
    internal static bool IsCall(SyntaxNode statement) =>
        statement is InstructionStatementSyntax instruction && Instructions.IsCall(instruction.MnemonicKind);

    /// <summary>
    /// Returns the call a branch makes, for a branch among <paramref name="calls"/>, or null for
    /// every other statement.
    /// </summary>
    internal static RelativeCall? RelativeCallIn(
        IReadOnlyDictionary<StepKey, RelativeCall> calls, Step step) =>
        calls.TryGetValue(step.Key, out var call) ? call : null;

    /// <summary>
    /// Returns a copy of this flow for another analysis of the program to compose. Composing sets
    /// what each routine costs with its calls and which registers it keeps, and an analysis that
    /// keeps this file from an earlier one composes into the copy. The earlier analysis, which a
    /// reader on another thread may still be using, then keeps the answers it had. The blocks and
    /// everything else worked out for the file alone are shared, because nothing changes them.
    /// </summary>
    internal ControlFlow ForComposing()
    {
        var copy = new ControlFlow(model, layout, annotations, relativeCalls, returnAddresses)
        {
            Diagnostics = Diagnostics,
            RunningOn = RunningOn,
            Registers = Registers,
        };
        copy.regions.AddRange(regions.Select(region => region.ForComposing()));
        return copy;
    }

    /// <summary>Returns the annotations under <paramref name="step"/>'s statement, in order.</summary>
    internal IReadOnlyList<StatementSyntax> AnnotationsOf(Step step) =>
        annotations.GetValueOrDefault(step.Key) ?? [];

    /// <summary>
    /// Returns the routine a statement calls, directly or as a relative call, or null for anything
    /// else.
    /// </summary>
    internal Symbol? CalledAt(Step step) => CalledAt(step, RelativeCallAt(step));

    /// <summary>
    /// Returns the routine a statement calls, directly or as the relative call
    /// <paramref name="relative"/>, or null for anything else.
    /// </summary>
    internal Symbol? CalledAt(Step step, RelativeCall? relative)
    {
        if (relative is { } known)
            return known.Routine;
        var statement = step.Statement;
        var mode = layout.Of(statement, step.On)?.Mode;
        return Transfers.Of(statement, mode) == Transfer.Call
            ? Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol
            : null;
    }

    /// <summary>
    /// Returns whether a statement calls a routine that never returns, which is where its path
    /// ends.
    /// </summary>
    internal bool CallsWhatNeverReturns(Step step) => CalledAt(step) is { Signature.NeverReturns: true };

    /// <summary>
    /// Returns the blocks that the state after <paramref name="block"/> flows on to, within one
    /// routine. A call's edge is to the routine it calls, which is checked against its signature
    /// rather than walked into, so after a call the state goes on only to the statement after it.
    /// A jump to a routine's entry, the routine's own included, leaves this routine, because it
    /// is a tail call.
    /// </summary>
    internal IEnumerable<int> Onward(IReadOnlyList<BasicBlock> blocks, BasicBlock block)
    {
        var calls = EndsInCall(block);
        foreach (var edge in block.Successors)
        {
            if (edge.Kind == EdgeKind.Call || (calls && edge.Kind != EdgeKind.FallThrough))
                continue;
            if (edge.Kind != EdgeKind.FallThrough && blocks[edge.To].Label is { Signature: not null })
                continue;
            yield return edge.To;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="block"/> ends in a call, made directly, through a pointer
    /// or as a relative call.
    /// </summary>
    internal bool EndsInCall(BasicBlock block) =>
        block.Steps.Count > 0 && (IsCall(block.Steps[^1].Statement) || RelativeCallAt(block.Steps[^1]) is not null);

    /// <summary>
    /// Returns the call a branch makes, for a branch that forms a relative call, or null for every
    /// other statement.
    /// </summary>
    internal RelativeCall? RelativeCallAt(Step step) => RelativeCallIn(relativeCalls, step);

    /// <summary>
    /// Returns whether a statement is the <c>per</c> that pushes a relative call's return address.
    /// </summary>
    internal bool IsReturnAddress(Step step) => returnAddresses.Contains(step.Key);

    /// <summary>
    /// Returns whether control continues into what follows. A call does, in any form, because it
    /// returns, unless it calls a routine that never returns. On any other statement a
    /// <c>.next</c> says where flow goes, and that replaces continuing past it.
    /// </summary>
    internal bool RunsOn(Unit unit) => RunsOn(unit, RelativeCallAt(unit.Step));

    /// <summary>
    /// Returns whether control continues into what follows, where <paramref name="relative"/> is
    /// the relative call the statement makes, if any. A call does, in any form, because it
    /// returns, unless it calls a routine that never returns. On any other statement a
    /// <c>.next</c> says where flow goes, and that replaces continuing past it.
    /// </summary>
    internal bool RunsOn(Unit unit, RelativeCall? relative)
    {
        if (unit.Step.Statement is FallthroughDirectiveSyntax)
            return false;
        if (CalledAt(unit.Step, relative) is { Signature.NeverReturns: true })
            return false;
        var transfer = Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode);
        if (transfer is Transfer.Call or Transfer.Elsewhere && IsCall(unit.Step.Statement))
            return true;
        if (relative is not null)
            return true;

        return unit.Next is null && transfer is Transfer.Through or Transfer.Branch or Transfer.Call;
    }

    /// <summary>
    /// Returns the labels one <c>.next</c> names. A target that is a list stands for every label in
    /// it. So does a data label whose items are all code labels or routines, each optionally minus
    /// one as in an RTS dispatch table. This is how an indirect call names the routines in its
    /// table.
    /// </summary>
    internal IEnumerable<(Symbol Symbol, Expansion? At)> Named(NextDirectiveSyntax next, Expansion? on)
    {
        foreach (var targetName in next.Targets)
        {
            // A `list` parameter names every label the call gave it.
            if (model.SymbolOf(targetName, on) is { Kind: SymbolKind.MacroParameter, Parameter.Kind: ParameterKind.List } list
                && model.GivenAt(list, on) is { Argument: var given, Caller: var caller })
            {
                foreach (var item in given.Items)
                {
                    if (Targets.Of(model, item, caller) is { } each)
                        yield return each;
                }
                continue;
            }

            if (Targets.Of(model, targetName, on) is not { } target)
                continue;
            var spread = Spread(target.Symbol, on).ToList();
            if (spread.Count > 0)
            {
                foreach (var item in spread)
                    yield return item;
                continue;
            }

            // Data that is not a table of code labels names nowhere code goes. It is reported
            // where the targets are checked rather than followed into the bytes.
            if (IsDataWithoutCodeLabels(target.Symbol, on))
                continue;
            yield return target;
        }
    }

    /// <summary>
    /// Returns the routine a <c>.fallthrough</c> names, or null where it names something else or
    /// nothing.
    /// </summary>
    internal Symbol? RoutineNamed(NameExpressionSyntax name, Expansion? on) =>
        Targets.Of(model, name, on)?.Symbol is { Kind: SymbolKind.Proc, Signature: not null } routine ? routine : null;

    /// <summary>
    /// Returns whether a symbol is data that does not spread to code labels, and so names nowhere
    /// code goes.
    /// </summary>
    internal bool IsDataWithoutCodeLabels(Symbol symbol, Expansion? on) =>
        symbol.Kind == SymbolKind.Data && !Spread(symbol, on).Any();

    /// <summary>
    /// Returns the routine emitted directly after <paramref name="region"/>'s routine in the same
    /// run of its segment's bytes, ignoring regions of other segments between them in the text.
    /// It also returns the <c>}</c> that closes this routine's body, which is where a
    /// <c>.fallthrough</c> naming the next routine goes. It returns null where anything with bytes
    /// in that segment, or nothing at all, comes next.
    /// </summary>
    internal (Symbol Routine, Span Closer)? EmittedAfter(FlowRegion region)
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
        var run = layout.PositionOf(region.Routine)?.Stream;
        for (var i = last + 1; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Segment != segment)
                continue;

            // An `.align` or a `.place` in between starts another run, and a routine in a
            // different run from this one's does not directly follow it.
            if (step.Label is { Kind: SymbolKind.Proc, Signature: not null } next && step.Statement is ProcDeclarationSyntax)
                return layout.PositionOf(next)?.Stream == run ? (next, closer.Tree.GetSpan(closer.Span)) : null;
            if (step.Label is not null || layout.PositionOf(step.Statement, step.On) is { Length: not 0 })
                return null;
        }
        return null;
    }

    /// <summary>
    /// Returns every <c>.scope</c> block inside a routine, as the span of the line that opens it
    /// and the span of the whole block. A <c>.scope</c> at file level is a namespace holding
    /// declarations, with no code of its own, so it is not included.
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

    /// <summary>Returns whether any block of a part of a routine calls.</summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks, bool[] inside)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (inside[i] && (blocks[i].Calls.Count > 0 || blocks[i].CallsUnknown))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns whether a routine calls anything, which its own cycle count does not follow into.
    /// Here a call counts only the call instruction, and what the called routine takes is that
    /// routine's own count. A <c>.fallthrough</c> into another routine is the same, since the
    /// count stops where that routine starts.
    /// </summary>
    private static bool Calls(IReadOnlyList<BasicBlock> blocks) =>
        blocks.Any(block => block.Calls.Count > 0 || block.RunsInto is not null || block.CallsUnknown);

    private static bool IsInstruction(SyntaxNode statement, params ReadOnlySpan<MnemonicKind> mnemonics) =>
        statement is InstructionStatementSyntax instruction && mnemonics.Contains(instruction.MnemonicKind);

    /// <summary>
    /// Returns whether a step takes no time because it generates nothing that runs. Such a step
    /// is a <c>.state</c>, a <c>.frame</c>, a <c>.fallthrough</c>, or a marker for either end of an
    /// expansion.
    /// </summary>
    private static bool TakesNoTime(Step step) =>
        step.Statement is StateDirectiveSyntax or FrameDirectiveSyntax or FallthroughDirectiveSyntax || step.IsMarker;

    /// <summary>
    /// Returns why a statement nt65 recognises has no count, for the editor's code lens. Without
    /// it the lens would show the routine without a count and give no reason. It returns null for
    /// a line nt65 could not lay out, which has already been reported where it appears.
    /// </summary>
    private static string? Uncounted(Step step) =>
        (step.Statement as InstructionStatementSyntax)?.MnemonicKind switch
        {
            MnemonicKind.Mvn or MnemonicKind.Mvp => "a block move takes 7 cycles per byte, and the number of bytes is in A",
            MnemonicKind.Jam => "`jam` stops the processor, and nothing after it runs until a reset",
            _ => null,
        };

    /// <summary>
    /// Returns why a routine has no count, taken from the first reached block that has no count.
    /// </summary>
    private static string? Uncounted(IReadOnlyList<BasicBlock> blocks) =>
        blocks.FirstOrDefault(block => block.IsReached && block.Uncounted is not null)?.Uncounted;

    /// <summary>Marks which blocks any path from the region's first block reaches.</summary>
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
    /// Returns a table item without the <c>- 1</c> of an RTS dispatch table, which names the same
    /// label either way.
    /// </summary>
    private static SyntaxNode Stripped(SyntaxNode item) =>
        item is BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference ? difference.Left : item;

    /// <summary>
    /// Returns what each inline <c>.scope</c> of a routine costs. A scope whose lines are blocks of
    /// their own costs what a pass through those blocks costs. A scope inside a single block costs
    /// what its own statements add up to, since a block runs all of it. A scope that is neither,
    /// whose first block nothing reaches, or that something branches into past its start, gets no
    /// cost, because there is no single pass through it to count.
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

    /// <summary>
    /// Returns what one pass through the part of a routine inside <paramref name="whole"/> costs.
    /// </summary>
    private RoutineCost? Cost(IReadOnlyList<BasicBlock> blocks, TextSpan whole)
    {
        if (ScopeShape.Of(blocks, whole) is not { } shape)
            return null;

        // The scope lies inside one block, so nothing in it branches. What it costs is what its
        // statements cost, and the block runs every one of them.
        if (shape.IsStraight)
            return Straight(blocks[shape.Entry], whole);

        var (entry, inside) = (shape.Entry, shape.Inside);
        var (minimum, maximum, ends) = Paths.Through(blocks, entry, at => inside[at], Paths.Costing);
        return minimum is null ? null : new RoutineCost(minimum, maximum, Calls(blocks, inside), ends);
    }

    /// <summary>
    /// Returns what the statements of <paramref name="block"/> that lie inside a span add up to.
    /// </summary>
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
        return any ? new RoutineCost(total.Minimum, total.Maximum, false, true) : null;
    }

    /// <summary>
    /// Returns the statements of one region, each with the annotations under it. An
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
            annotations[unit.Step.Key] = unit.Annotations;
        return units;
    }

    /// <summary>
    /// Finds the branches that are calls. Such a branch is <c>brl f</c> or <c>bra f</c> to a
    /// routine, directly after <c>per L-1</c>, where <c>L</c> is the label directly after the
    /// branch. A far call also has a <c>phk</c> directly before the <c>per</c>.
    /// </summary>
    /// <returns>
    /// The call each such branch makes, and the <c>per</c> statements that push the calls' return
    /// addresses.
    /// </returns>
    private (Dictionary<StepKey, RelativeCall> Calls, List<StepKey> ReturnAddresses)
        FindRelativeCalls(IReadOnlyList<Unit> units)
    {
        var calls = new Dictionary<StepKey, RelativeCall>();
        var returnAddresses = new List<StepKey>();
        for (var i = 1; i + 1 < units.Count; i++)
        {
            var branch = units[i].Step;
            var push = units[i - 1];
            if (push.Step.Stream != branch.Stream || units[i + 1].Step.Stream != branch.Stream
                || !IsInstruction(branch.Statement, MnemonicKind.Brl, MnemonicKind.Bra)
                || !IsInstruction(push.Step.Statement, MnemonicKind.Per) || push.Next is not null || units[i].Next is not null
                || units[i + 1].Step.Label is not { } after
                || Targets.Of(model, Transfers.TargetOf(branch.Statement, AddressingMode.Relative), branch.On)
                    is not { Symbol.Signature: not null } routine
                || !NamesTheAddressBefore(push.Step, after))
            {
                continue;
            }
            var far = i >= 2 && units[i - 2].Step.Stream == branch.Stream
                && IsInstruction(units[i - 2].Step.Statement, MnemonicKind.Phk);
            calls[branch.Key] = new RelativeCall(routine.Symbol, far);
            returnAddresses.Add(push.Step.Key);
        }
        return (calls, returnAddresses);
    }

    /// <summary>
    /// Returns whether a <c>per</c> pushes <c>L-1</c>, the byte before <paramref name="label"/>, as
    /// a return address does.
    /// </summary>
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

    /// <summary>
    /// Returns the blocks of one region. A label starts a block, and a statement that transfers
    /// control ends one, so if any of a block runs, all of it runs.
    /// </summary>
    /// <param name="units">The statements of the region.</param>
    /// <param name="calls">The relative calls among the statements, from <see cref="FindRelativeCalls"/>.</param>
    /// <param name="inlineData">
    /// The data that calls return past, from <see cref="FlowChecks.FindInlineData"/>. The processor
    /// never runs it, so it costs nothing, rather than leaving its block without a count.
    /// </param>
    private List<BasicBlock> Blocks(
        IReadOnlyList<Unit> units, IReadOnlyDictionary<StepKey, RelativeCall> calls, HashSet<Unit> inlineData)
    {
        var skipped = inlineData.Select(unit => unit.Step.Key).ToHashSet();
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
            runsOn = RunsOn(unit, RelativeCallIn(calls, unit.Step));
        }

        Link(blocks, tails, fallenInto, found, calls);
        Reach(blocks);
        for (var i = 0; i < blocks.Count; i++)
        {
            blocks[i].IsFallenInto = fallenInto[i];
            blocks[i].Next = tails[i]?.Next;
            blocks[i].RunsInto = tails[i]?.Step is { Statement: FallthroughDirectiveSyntax { Target: { } into } } end
                ? RoutineNamed(into, end.On)
                : null;
            blocks[i].Cycles = Counted(blocks[i], skipped);
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
    /// Adds the edges between blocks. Fall-through is already known from how the blocks were cut.
    /// What is left is where each block's last statement says control goes.
    /// </summary>
    private void Link(
        List<BasicBlock> blocks, List<Unit?> tails, List<bool> fallenInto,
        Dictionary<(Symbol Symbol, Expansion? At), int> found, IReadOnlyDictionary<StepKey, RelativeCall> calls)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i + 1 < blocks.Count && fallenInto[i + 1])
                Edge(i, i + 1, EdgeKind.FallThrough);
            if (tails[i] is not { } tail)
                continue;

            var mode = layout.Of(tail.Step.Statement, tail.Step.On)?.Mode;
            var transfer = Transfers.Of(tail.Step.Statement, mode);
            var relative = RelativeCallIn(calls, tail.Step);
            var makesCall = transfer == Transfer.Call || relative is not null;

            // A `.next` replaces the operand as the source of the targets, because the operand
            // does not identify them. On a call it lists the routines called, and the call
            // still returns to the statement after it.
            if (tail.Next is { } next)
            {
                foreach (var named in Named(next, tail.Step.On))
                {
                    if (found.TryGetValue(named, out var to))
                        Edge(i, to, EdgeKind.Declared);
                    if (makesCall)
                        blocks[i].Called(named.Symbol);
                }
                continue;
            }

            if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call))
            {
                // A `jsr` whose target the operand does not name is still a call, and one that
                // nothing here can follow into.
                blocks[i].CallsUnknown |= transfer == Transfer.Elsewhere && IsCall(tail.Step.Statement);
                continue;
            }
            var target = Targets.Of(model, Transfers.TargetOf(tail.Step.Statement, mode), tail.Step.On);
            var inside = false;
            if (target is { } resolved && found.TryGetValue(resolved, out var landing))
            {
                inside = true;
                Edge(i, landing, makesCall ? EdgeKind.Call : EdgeKind.Taken);
            }
            blocks[i].BranchesOut = transfer == Transfer.Branch && !inside && relative is null;

            // What a call reaches costs what that routine costs, and so does what a tail jump
            // reaches, since control comes back from it to this routine's caller. A target
            // inside this routine is neither, because the path simply continues into it.
            if (relative is { } known)
                blocks[i].Called(known.Routine);
            else if (makesCall || (transfer == Transfer.Jump && !inside))
                blocks[i].SetCalled(target?.Symbol);
        }

        void Edge(int from, int to, EdgeKind kind)
        {
            blocks[from].Reach(to, kind);
            blocks[to].ReachedFrom(from);
        }
    }

    /// <summary>
    /// Returns how long the whole block takes. A block runs all of it or none, so the counts add
    /// up. One statement nt65 has no count for leaves the block without one. The data in
    /// <paramref name="skipped"/> is returned past rather than run, so it costs nothing.
    /// </summary>
    private CycleCount? Counted(BasicBlock block, HashSet<StepKey> skipped)
    {
        var total = new CycleCount(0);
        foreach (var step in block.Steps)
        {
            if (TakesNoTime(step) || skipped.Contains(step.Key))
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
    /// Returns whether a statement is the last of its block, because control leaves after it. A
    /// call leaves too, even though it comes back. What it reaches is an edge of its own, and an
    /// edge leaves a block at its end.
    /// </summary>
    private bool EndsBlock(Unit unit) =>
        unit.Next is not null || unit.Step.Statement is FallthroughDirectiveSyntax
        || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
            != Transfer.Through;

    /// <summary>
    /// Returns the labels a table or a list expands to. The result is empty when the target does
    /// not expand to labels and so names only itself.
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
    /// Returns the values of a table, which is data declared as addresses with <c>.addr</c> or
    /// <c>.faraddr</c>, each paired with the <see cref="Expansion"/> it is in. Values in a body are
    /// read from the expansions layout made of them, since each iteration of a repetition there
    /// may emit a value differently. Any other symbol yields no values.
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

    /// <summary>Represents one statement and the annotations under it.</summary>
    internal sealed class Unit(Step step)
    {
        /// <summary>Gets the statement itself.</summary>
        public Step Step { get; } = step;

        /// <summary>Gets the annotations under it, in source order.</summary>
        public List<StatementSyntax> Annotations { get; } = [];

        /// <summary>Gets the <c>.next</c> among them, or null when there is none.</summary>
        public NextDirectiveSyntax? Next => Annotations.OfType<NextDirectiveSyntax>().FirstOrDefault();
    }
}
