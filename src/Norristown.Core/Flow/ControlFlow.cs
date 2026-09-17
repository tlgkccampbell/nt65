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
/// 6502 and its CMOS variants nothing consumes processor state, so no annotation is required.
/// </para>
/// </summary>
public sealed class ControlFlow
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly List<FlowRegion> regions = [];
    private readonly Dictionary<(int Position, Expansion? On), IReadOnlyList<SyntaxNode>> annotations = [];
    private readonly Dictionary<(int Position, Expansion? On), RelativeCall> relativeCalls = [];
    private readonly HashSet<(int Position, Expansion? On)> returnAddresses = [];

    private ControlFlow(SemanticModel model, CodeLayout layout)
    {
        this.model = model;
        this.layout = layout;
    }

    /// <summary>Every routine in the file, one region each.</summary>
    public IReadOnlyList<FlowRegion> Regions => regions;

    /// <summary>What is wrong with the paths through this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Works out where control goes in <paramref name="layout"/>'s file.</summary>
    public static ControlFlow Of(SemanticModel model, CodeLayout layout)
    {
        var flow = new ControlFlow(model, layout);
        var diagnostics = new List<Diagnostic>();

        // A routine's bytes are one stream unless a nested segment block takes some of them
        // somewhere else. Fall-through stays inside a stream, but a jump may go from one to
        // another, so all of a routine's streams are one graph, its own stream first.
        foreach (var run in layout.Steps.Where(step => step.Routine is not null).GroupBy(step => step.Routine))
        {
            var routine = run.Key!;
            var units = flow.Units([.. run.GroupBy(step => step.Stream).SelectMany(stream => stream)]);
            flow.FindRelativeCalls(units);
            var blocks = flow.Blocks(units);

            // The routine is entered where its own name stands.
            var entered = blocks.Count > 0 && blocks[0].Label == routine;
            var region = new FlowRegion(routine, entered, blocks);
            flow.regions.Add(region);
            flow.CheckTargets(units, diagnostics);
            flow.CheckUnreachableLabels(region, diagnostics);
            flow.CheckDataReachedByFallingThrough(units, flow.CheckInlineData(units, diagnostics), diagnostics);
            flow.CheckRunningOn(units, diagnostics);
            flow.CheckReturnsAndCalls(routine, units, diagnostics);
        }

        // On the 65816 the analysis consumes what flow it cannot see, so each construct that
        // hides some has to say what it hides.
        if (layout.Cpu == Project.Cpu.Wdc65816)
            Requirements.Check(model, layout, flow, diagnostics);
        else
            Requirements.CheckEnds(model, layout, flow, diagnostics);

        flow.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
        return flow;
    }

    /// <summary>The annotations written under <paramref name="step"/>'s statement, in order.</summary>
    internal IReadOnlyList<SyntaxNode> AnnotationsOf(Step step) =>
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
            if (Annotations.Is(step.Statement) && units.FindLast(unit => !unit.Step.IsMarker) is { } above)
            {
                above.Annotations.Add(step.Statement);
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
        if (push.Statement.ChildNodes.FirstOrDefault()?.ChildNodes.FirstOrDefault() is not
            { Kind: SyntaxKind.BinaryExpression, ChildNodes: [var left, var right] } difference
            || !difference.ChildTokens.Any(token => token.Kind == SyntaxKind.Minus))
        {
            return false;
        }
        return Targets.Of(model, left, push.On)?.Symbol == label
            && model.ValueOf(right, push.On).AsNumber() == 1;
    }

    private static bool IsInstruction(SyntaxNode statement, string mnemonic) =>
        statement.Kind == SyntaxKind.InstructionStatement && statement.ChildTokens.Length > 0
        && statement.ChildTokens[0].Text.Equals(mnemonic, StringComparison.OrdinalIgnoreCase);

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
            blocks[i].Cycles = Timing(blocks[i]);
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
            var calls = transfer == Transfer.Call || RelativeCallAt(tail.Step) is not null;
            if (Targets.Of(model, Transfers.TargetOf(tail.Step.Statement, mode), tail.Step.On) is { } target
                && found.TryGetValue(target, out var reached))
            {
                Edge(i, reached, calls ? EdgeKind.Call : EdgeKind.Taken);
            }
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
    private CycleCount? Timing(BasicBlock block)
    {
        var total = new CycleCount(0);
        foreach (var step in block.Steps)
        {
            // A `.state` and a `.frame` take no time, because they are not there at all, and
            // neither do the ends of an expansion.
            if (step.Statement.Kind is SyntaxKind.StateDirective or SyntaxKind.FrameDirective || step.IsMarker)
                continue;
            if (layout.Of(step.Statement, step.On)?.Cycles is not { } cycles)
                return null;
            total += cycles;
        }
        return block.Steps.Count == 0 ? null : total;
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
    /// items are all code labels or routines — each optionally minus one, as an RTS dispatch
    /// table writes them — stands for every one of those labels, which is how an indirect
    /// call names the routines in its table.
    /// </summary>
    internal IEnumerable<(Symbol Symbol, Expansion? At)> Named(SyntaxNode next, Expansion? on)
    {
        foreach (var written in Annotations.TargetsOf(next))
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

    /// <summary>The labels a table or a list stands for, empty when it stands only for itself.</summary>
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
    /// The values of a table: data declared as addresses, `.addr` or `.faraddr`, and each value
    /// with the writing it is on. Values in a body are read from the writings layout made of
    /// them, since a repetition there may write each one differently. Anything else spreads to
    /// nothing.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsOfTable(Symbol target)
    {
        if (!IsAddressData(target) || target.Data is not { } element)
            yield break;
        foreach (var value in DataLengths.ElementsOf(element))
            yield return (value, null);
        if (DataSyntax.BodyOf(element) is null)
            yield break;
        foreach (var step in layout.Steps)
        {
            if (step.Statement.Kind == SyntaxKind.DataValues && DataSyntax.DirectiveOfValues(step.Statement) == element)
            {
                foreach (var value in step.Statement.ChildNodes)
                    yield return (value, step.On);
            }
        }
    }

    /// <summary>Whether a symbol is data declared as addresses, which is what a table of targets is.</summary>
    private static bool IsAddressData(Symbol symbol) =>
        symbol is { Kind: SymbolKind.Data, Data: { } element } && DataSyntax.NameOf(element) is ".addr" or ".faraddr";

    /// <summary>Whether a name is data that does not spread to code labels, which names nowhere code goes.</summary>
    private bool IsDataWithoutCodeLabels(Symbol symbol, Expansion? on) =>
        symbol.Kind == SymbolKind.Data && !Spread(symbol, on).Any();

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
                    diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span), Severity.Error,
                        IsAddressData(target.Symbol)
                            ? $"`{target.Symbol.DisplayName}` holds no code labels, and `.next` reads the labels a table holds"
                            : $"`{target.Symbol.DisplayName}` is not a table of addresses: `.next` reads the labels a table "
                                + "declared as `.addr` or `.faraddr` holds"));
                    continue;
                }
                if (target.Symbol.IsAddress || target.Symbol.Kind == SymbolKind.List)
                    continue;
                diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span), Severity.Error,
                    $"`{target.Symbol.DisplayName}` is a {target.Symbol.KindText}, and "
                    + $"`{Annotations.Spell(annotation.a)}` names somewhere code is"));
            }
        }
    }

    /// <summary>
    /// A label nothing runs into and nothing names. Recognition is complete for what is
    /// written, so once the label exists the checks cover it; this is what pushes the
    /// programmer to write it down. A <c>.state</c> directly after the label declares it as
    /// an entry point, which acknowledges that it is reached from somewhere nt65 cannot see.
    /// A label on data is read rather than run, so nothing reaching it is no news.
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
                && block.Steps is [{ Statement.Kind: SyntaxKind.InstructionStatement } first, ..])
            {
                diagnostics.Add(new Diagnostic(first.Statement.Tree.GetSpan(first.Statement.Span), Severity.Warning,
                    "this code is never reached: fall-through does not enter a nested segment block, so code "
                    + "there starts at a label a `.next` names or a `.state` declares"));
                continue;
            }
            if (block.Index == 0 || block.Label is not { Kind: not SymbolKind.Data } label || block.Predecessors.Count > 0
                || block.IsDeclared || block.Steps is [{ Statement.Kind: SyntaxKind.DataDirective or SyntaxKind.DataValues }, ..])
            {
                continue;
            }
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
    private void CheckDataReachedByFallingThrough(
        IReadOnlyList<Unit> units, HashSet<Unit> inline, List<Diagnostic> diagnostics)
    {
        // On the 65816 flow that runs into data reaches the analysis, which cannot follow it,
        // so there the annotation is required rather than suggested.
        var severity = layout.Cpu == Project.Cpu.Wdc65816 ? Severity.Error : Severity.Warning;
        var fromCode = false;
        int? stream = null;
        foreach (var unit in units)
        {
            if (unit.Step.Stream != stream)
            {
                fromCode = false;
                stream = unit.Step.Stream;
            }
            if (unit.Step.Label is not null || unit.Step.IsMarker)
                continue;
            var data = unit.Step.Statement.Kind is SyntaxKind.DataDirective or SyntaxKind.DataValues;

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
                    "the instruction above runs into this data. `.next` on it says where flow goes instead"));
            }

            // A run of data is one run: only what code runs into is worth saying.
            fromCode = !data && RunsOn(unit);
        }
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
                    && units[i + 1].Step.Statement is { Kind: SyntaxKind.DataDirective } text
                    && text.ChildTokens.Length > 0
                    && text.ChildTokens[0].Text.Equals(".strz", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(units[i + 1]);
                }
                else
                {
                    Report(call, $"`{name}` returns past one `.strz` written after each call, and none follows this one");
                }
                continue;
            }

            if (inline.Expression is not { } count
                || model.ValueOf(count, units[i].Step.On).AsNumber() is not { } bytes || bytes < 0)
            {
                Report(call, $"`{name}` returns past `{inline.Text}`, which needs a constant count of bytes");
                continue;
            }

            // The data is the run of it directly after the call, as long as it takes to make up
            // the count.
            var taken = 0L;
            for (var j = i + 1; j < units.Count && taken < bytes; j++)
            {
                if (units[j].Step.Label is not null || units[j].Step.Stream != units[i].Step.Stream
                    || units[j].Step.Statement.Kind is not (SyntaxKind.DataDirective or SyntaxKind.DataValues))
                    break;
                taken += layout.Of(units[j].Step.Statement, units[j].Step.On)?.Length ?? 0;
                skipped.Add(units[j]);
            }
            if (taken != bytes)
            {
                Report(call, $"`{name}` returns past {Bytes(bytes)} of data written after each call, and "
                    + (taken == 0 ? "none follows this one" : $"{Bytes(taken)} {(taken == 1 ? "follows" : "follow")} this one"));
            }
        }
        return skipped;

        void Report(SyntaxNode node, string message) =>
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
                && (IsInstruction(statement, "rts") || IsInstruction(statement, "rtl")))
            {
                var returned = statement.ChildTokens[0].Text.ToLowerInvariant();
                Report(statement, own.IsInterrupt
                    ? $"`{routine.DisplayName}` is an interrupt handler, and leaves by `rti` rather than `{returned}`"
                    : $"`{routine.DisplayName}` never returns, as its `-> none` says, and `{returned}` returns");
            }
            if (CalledAt(unit) is { Signature.IsInterrupt: true } handler)
            {
                Report(statement, $"`{handler.DisplayName}` is an interrupt handler, which the processor enters and `rti` "
                    + "leaves: a call to it would not come back");
            }
        }

        void Report(SyntaxNode node, string message) =>
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));
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

    /// <summary>
    /// A <c>.next</c> that names a routine where flow would otherwise run on says that flow
    /// runs into that routine, which is true only when the routine starts where the statement
    /// ends, in the same stream of bytes.
    /// </summary>
    private void CheckRunningOn(IReadOnlyList<Unit> units, List<Diagnostic> diagnostics)
    {
        foreach (var unit in units)
        {
            if (unit.Next is not { } next || unit.Step.Label is not null
                || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
                    != Transfer.Through)
            {
                continue;
            }
            var end = layout.Placed(unit.Step.Statement, unit.Step.On);
            foreach (var written in Annotations.TargetsOf(next))
            {
                if (Targets.Of(model, written, unit.Step.On) is not { Symbol: { Signature: not null } routine })
                    continue;
                if (end is { } here && layout.Placed(routine) is { } there
                    && there.Stream == here.Stream && there.Offset == here.End)
                {
                    continue;
                }
                diagnostics.Add(new Diagnostic(written.Tree.GetSpan(written.Span), Severity.Error,
                    $"`.next {routine.DisplayName}` says flow runs on into `{routine.DisplayName}`, and it does not "
                    + "start where this statement ends: a routine runs into the one written directly after it"));
            }
        }
    }

    /// <summary>
    /// Whether a statement is the last of its block, because control leaves after it. A call
    /// leaves too, even though it comes back: what it reaches is an edge of its own, and an
    /// edge leaves a block at its end.
    /// </summary>
    private bool EndsBlock(Unit unit) =>
        unit.Next is not null
        || Transfers.Of(unit.Step.Statement, layout.Of(unit.Step.Statement, unit.Step.On)?.Mode)
            != Transfer.Through;

    /// <summary>
    /// Whether control carries on into whatever follows. A call does however it is written,
    /// because it comes back, unless it calls a routine that never returns; a <c>.next</c> on
    /// anything else says where flow goes, and past the statement is not it.
    /// </summary>
    private bool RunsOn(Unit unit)
    {
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
