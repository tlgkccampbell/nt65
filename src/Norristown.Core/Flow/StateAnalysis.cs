using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// The 65816's register widths, emulation flag, direct page and data bank, tracked through
/// each routine. 65816 code cannot be written without them: the widths decide how wide an
/// immediate is, and D and B what memory an operand reaches. The analysis stays small because
/// the language keeps it inside one routine: every routine declares its state at entry and
/// exit, and control either stays in the routine or goes to another routine's entry.
/// <para>
/// Each region's blocks are run to a fixed point over a lattice of known values and
/// unknown, starting from the routine's signature and from every label a <c>.state</c>
/// declares. What is reported comes from one pass over the converged states: a merge never
/// reports, and an unknown value is an error only where it is used. What this type works out
/// is what each statement does to the state; <see cref="StateChecks"/> is what says what is
/// wrong with it.
/// </para>
/// </summary>
public sealed class StateAnalysis
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly StateChecks checks;
    private readonly Dictionary<(int Position, Expansion? On), FlowState> reaching = [];
    private readonly Dictionary<(int Position, Expansion? On), int> slots = [];

    // The state at the start of each expansion of a macro with a signature, and of each block
    // spliced into one, which is what the end of it is checked against and hands back.
    private readonly Dictionary<(int Position, Expansion? On), ProcessorState> started = [];

    // The labels this file names from a routine other than the one they are in, worked out the
    // first time a declared label asks and kept for the rest of them.
    private HashSet<Symbol>? namedFromOutside;

    private StateAnalysis(SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange> ranges)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        checks = new StateChecks(model, layout, ranges);
    }

    /// <summary>What is wrong with the widths, the mode and the calls in this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// The most times any one block was walked before its region settled, counting the walk
    /// that found nothing had changed. This is what measures how quickly the analysis
    /// converges.
    /// </summary>
    public int MostWalks { get; private set; }

    /// <summary>
    /// Works out the processor state through every routine of <paramref name="flow"/>'s file.
    /// <paramref name="ranges"/> is the project's table of which banks each range of absolute
    /// addresses may be reached from.
    /// </summary>
    public static StateAnalysis Of(
        SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange>? ranges = null)
    {
        var analysis = new StateAnalysis(model, layout, flow, ranges ?? []);
        foreach (var region in flow.Regions)
            analysis.Analyze(region);
        analysis.checks.Final = true;
        analysis.checks.CheckOutsideRoutines();
        analysis.Diagnostics = Norristown.Diagnostics.Ordered(
            analysis.checks.Found.DistinctBy(d => (d.Span, d.Message)));
        return analysis;
    }

    /// <summary>
    /// The state reaching <paramref name="statement"/> on the writing <paramref name="on"/>,
    /// or null where nothing reaches it or it is in no routine.
    /// </summary>
    public FlowState? Before(SyntaxNode statement, Expansion? on = null) =>
        reaching.GetValueOrDefault((statement.Position, on));

    /// <summary>
    /// The <c>n</c> of <c>n,s</c> that the frame slot in <paramref name="statement"/>'s operand
    /// comes to on the writing <paramref name="on"/>, or null where it names no slot or the
    /// slot is not known.
    /// </summary>
    public int? SlotAt(SyntaxNode statement, Expansion? on = null) =>
        slots.TryGetValue((statement.Position, on), out var slot) ? slot : null;

    /// <summary>
    /// The state reaching a statement, whichever writing of it is asked about. An editor asks
    /// about a line, and is shown the state that reaches its first writing. A writing's line is
    /// in the file of the block it writes out, which is another file's for a macro declared
    /// there, so the same position in two files is two lines.
    /// </summary>
    public FlowState? AnyBefore(SyntaxNode statement) =>
        reaching.Where(pair => pair.Key.Position == statement.Position
                && (pair.Key.On?.Body?.Tree ?? model.Tree) == statement.Tree)
            .Select(pair => pair.Value)
            .FirstOrDefault();

    /// <summary>
    /// What a routine's state is when it is entered: its declared entry, with nothing pushed but,
    /// for a routine that takes <c>args n</c>, the arguments and the return address above them.
    /// </summary>
    private static FlowState Entry(Signature signature, Symbol routine) => new(
        signature.Entry,
        signature.Arguments > 0
            ? AnalysisStack.Empty.Push(StackEntry.Opaque, signature.Arguments + (signature.IsFar ? 3 : 2))
            : AnalysisStack.Empty)
    {
        WhyA = signature.Entry.A == Width.Unknown ? EntryCause(signature, routine, "a?") : null,
        WhyIndex = signature.Entry.Index == Width.Unknown ? EntryCause(signature, routine, "i?") : null,
    };

    private static WidthCause EntryCause(Signature signature, Symbol routine, string item) => signature.IsInterrupt
        ? new($"`{routine.DisplayName}` is an interrupt handler, entered from anywhere", "an `.ensure` sets it")
        : new($"`{routine.DisplayName}` says `{item}` at entry", "an `.ensure` sets it");

    /// <summary>How many bytes a push or pull of a register this wide moves, or null when that is not known.</summary>
    private static int? Bytes(Width width) => width switch
    {
        Width.Eight => 1,
        Width.Sixteen => 2,
        _ => null,
    };

    private static bool Is(SyntaxNode statement, string mnemonic) =>
        statement is InstructionStatementSyntax instruction
        && instruction.Mnemonic.Text.Equals(mnemonic, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a <c>dp = e</c> or <c>dbr = e</c> item says, or unknown for <c>dp?</c> and for a value nt65 cannot work out.</summary>
    private static StateValue ValueOf(StateItem item, SemanticModel model) =>
        item.Expression is { } expression && model.ValueOf(expression).AsNumber() is { } value
            ? StateValue.Of(value)
            : StateValue.Unknown;

    /// <summary>
    /// What is known where control arrives from outside the routine's own paths: nothing,
    /// except that a part the routine promises to hand back unchanged is taken to be left alone
    /// on the way there too, so only a routine that declares a part needs its labels to.
    /// </summary>
    private static ProcessorState Outside(Signature signature)
    {
        var entry = signature.Entry;
        return new ProcessorState(
            entry.A == Width.Unchanged ? Width.Unchanged : Width.Unknown,
            entry.Index == Width.Unchanged ? Width.Unchanged : Width.Unknown,
            entry.E == ProcessorMode.Unchanged ? ProcessorMode.Unchanged : ProcessorMode.Unknown,
            entry.D.Kind == StateValueKind.Unchanged ? StateValue.Unchanged : StateValue.Unknown,
            entry.B.Kind == StateValueKind.Unchanged ? StateValue.Unchanged : StateValue.Unknown);
    }

    /// <summary>
    /// Which parts of the state a declared label's <c>.state</c> gives, which are the parts a
    /// jump into it is checked for and so the only parts it may assume. <c>?</c> gives them all,
    /// as unknown.
    /// </summary>
    private static HashSet<StatePart> Given(BasicBlock block)
    {
        var given = new HashSet<StatePart>();
        foreach (var item in StateItem.Read(block.Steps[0].Statement))
        {
            if (item.Part == StatePart.AllUnknown)
            {
                given.UnionWith(
                    [StatePart.A, StatePart.Index, StatePart.E, StatePart.DirectPage, StatePart.DataBank]);
            }
            else
            {
                given.Add(item.Part);
            }
        }
        return given;
    }

    /// <summary>Why a width at a declared label is unknown: the declaration does not say.</summary>
    private static WidthCause Undeclared(Symbol label, Symbol routine, string register, string item) => new(
        $"`{label.DisplayName}` can be entered from outside `{routine.DisplayName}`, and its `.state` does not "
            + $"say the width of {register}",
        $"the `.state` after `{label.DisplayName}` can say `{item}8` or `{item}16`");

    private static AnalysisStack? Push(AnalysisStack? stack, int? bytes) =>
        bytes is { } count ? stack?.Push(StackEntry.Opaque, count) : null;

    private static AnalysisStack? Pull(AnalysisStack? stack, int? bytes) =>
        bytes is { } count ? stack?.Pull(count) : null;

    /// <summary>
    /// One region to a fixed point, then once more to report. Blocks are taken lowest index
    /// first, which is the order their bytes are written in, so most of a routine settles in
    /// a single pass.
    /// </summary>
    private void Analyze(FlowRegion region)
    {
        var blocks = region.Blocks;
        var signature = region.Routine.Signature ?? Signature.Default;
        var reached = new FlowState?[blocks.Count];
        var walks = new int[blocks.Count];
        var pending = new SortedSet<int>();

        if (region.IsEntered && blocks.Count > 0)
        {
            reached[0] = Entry(signature, region.Routine);
            pending.Add(0);
        }
        Settle();

        // A label a `.state` declares is an entry point in its own right. One nothing reaches
        // starts from what the directive says, over a state otherwise unknown. One some path
        // already reaches is checked against that path, and, where the label can also be
        // entered from outside the routine, keeps only the parts the declaration gives: what
        // the paths inside leave is no promise to whoever jumps in.
        foreach (var block in blocks)
        {
            if (!block.IsDeclared)
                continue;
            if (reached[block.Index] is null)
            {
                reached[block.Index] = new FlowState(Outside(signature), null);
            }
            else if (EnteredFromOutside(block, region))
            {
                var entered = Entered(block, reached[block.Index]!, signature, region.Routine);
                if (entered.Equals(reached[block.Index]))
                    continue;
                reached[block.Index] = entered;
            }
            else
            {
                continue;
            }
            pending.Add(block.Index);
            Settle();
        }

        checks.Final = true;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is { } state)
                Walk(block, state, region);
            else
                checks.Unreached(block, region);
        }
        checks.Final = false;
        MostWalks = Math.Max(MostWalks, walks.DefaultIfEmpty().Max());

        void Settle()
        {
            while (pending.Count > 0)
            {
                var index = pending.Min;
                pending.Remove(index);
                walks[index]++;
                var block = blocks[index];
                var after = Walk(block, reached[index]!, region);
                foreach (var edge in Carried(block))
                {
                    var merged = FlowState.Merge(reached[edge], after);
                    if (merged.Equals(reached[edge]))
                        continue;
                    reached[edge] = merged;
                    pending.Add(edge);
                }
            }
        }

        // The edges the state after a block travels along. A call's edge is to the routine it
        // calls, which is checked against its signature rather than walked into, and so is a
        // jump to a routine's entry, the routine's own included: that is a tail call.
        IEnumerable<int> Carried(BasicBlock block)
        {
            var calls = block.Steps.Count > 0 && IsCallOrIndirectCall(block.Steps[^1]);
            foreach (var edge in block.Successors)
            {
                if (edge.Kind == EdgeKind.Call || (calls && edge.Kind != EdgeKind.FallThrough))
                    continue;
                if (edge.Kind != EdgeKind.FallThrough && blocks[edge.To].Label is { Signature: not null })
                    continue;
                yield return edge.To;
            }
        }
    }

    /// <summary>
    /// Whether control may reach a declared label from outside the routine it is in: another
    /// module may jump to an exported one, and this file may name one from another routine.
    /// A sibling joined by <c>.next</c> is part of the same routine as far as state goes, so a
    /// path from one is not from outside.
    /// </summary>
    private bool EnteredFromOutside(BasicBlock block, FlowRegion region)
    {
        if (block.Label is not { } label)
            return false;
        if (label.IsExported)
            return true;
        namedFromOutside ??= NamedFromOutside();
        return namedFromOutside.Contains(label);
    }

    /// <summary>
    /// Every label this file names from a routine other than the one the label is in: a jump
    /// into another routine, and a path naming one as data.
    /// </summary>
    private HashSet<Symbol> NamedFromOutside()
    {
        var found = new HashSet<Symbol>();
        foreach (var step in layout.Steps)
        {
            if (step.Routine is not { } routine)
                continue;
            foreach (var name in step.Statement.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if (Targets.Of(model, name, step.On)?.Symbol is { Kind: SymbolKind.Label, Routine: { } owner } label
                    && owner != routine && !owner.IsSiblingOf(routine))
                {
                    found.Add(label);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The state at a declared label that can be entered from outside the routine: the parts
    /// its <c>.state</c> gives keep what reaches the label, which the directive itself then
    /// checks, and every part it leaves out becomes unknown, because a jump from outside is
    /// checked for the parts the declaration gives and for nothing else.
    /// </summary>
    private FlowState Entered(BasicBlock block, FlowState reached, Signature signature, Symbol routine)
    {
        var given = Given(block);
        var here = reached.Processor;
        var outside = Outside(signature);
        var label = block.Label!;
        var a = given.Contains(StatePart.A) ? here.A : Met(here.A, outside.A);
        var index = given.Contains(StatePart.Index) ? here.Index : Met(here.Index, outside.Index);
        return reached with
        {
            Processor = new ProcessorState(
                a,
                index,
                given.Contains(StatePart.E) ? here.E : here.E == outside.E ? here.E : ProcessorMode.Unknown,
                given.Contains(StatePart.DirectPage) ? here.D : StateValue.Merge(here.D, outside.D),
                given.Contains(StatePart.DataBank) ? here.B : StateValue.Merge(here.B, outside.B)),
            WhyA = a == Width.Unknown && !given.Contains(StatePart.A)
                ? Undeclared(label, routine, "A", "a")
                : reached.WhyA,
            WhyIndex = index == Width.Unknown && !given.Contains(StatePart.Index)
                ? Undeclared(label, routine, "X and Y", "i")
                : reached.WhyIndex,
        };

        static Width Met(Width here, Width outside) => here == outside ? here : Width.Unknown;
    }

    /// <summary>The state through one block, from the state that reaches it.</summary>
    private FlowState Walk(BasicBlock block, FlowState state, FlowRegion region)
    {
        var routine = region.Routine;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            if (checks.Final && !step.Closes)
                reaching[(step.Statement.Position, step.On)] = state;
            Step? previous = i > 0 ? block.Steps[i - 1] : null;
            var next = i == block.Steps.Count - 1 ? block.Next : null;
            state = Explained(step, next, state, Through(step, previous, next, state, routine));
        }
        return state;
    }

    /// <summary>
    /// The state after <paramref name="step"/>, with a cause for each width it made unknown and
    /// the cause carried on for each it left unknown.
    /// </summary>
    private FlowState Explained(Step step, NextDirectiveSyntax? next, FlowState before, FlowState after) => after with
    {
        WhyA = Why(step, next, before.Processor.E, before.Processor.A, after.Processor.A, before.WhyA),
        WhyIndex = Why(step, next, before.Processor.E, before.Processor.Index, after.Processor.Index, before.WhyIndex),
    };

    private WidthCause? Why(
        Step step, NextDirectiveSyntax? next, ProcessorMode mode, Width before, Width after, WidthCause? carried)
    {
        if (after != Width.Unknown)
            return null;
        if (before == Width.Unknown)
            return carried;

        if (step.Statement is StateDirectiveSyntax)
            return new("a `.state` says so", "the `.state` can say what it is");
        if (step.Statement is not InstructionStatementSyntax statement)
            return null;
        var written = $"`{statement.GetText().Trim()}`";
        return statement.Mnemonic.Text.ToLowerInvariant() switch
        {
            "plp" => new($"{written} pulls a status that no `php` in this routine pushed", "an `.ensure` after it sets it"),
            "xce" => new($"{written} follows neither `clc` nor `sec`", "a `.state` after it says what it is"),
            "rep" when mode != ProcessorMode.Native && Constant(step) is not null
                => new($"{written} widens nothing in emulation mode, and the mode is not known", "a `.state` before it says which mode it is"),
            "rep" or "sep" => new($"{written} changes flags nt65 cannot work out", "an `.ensure` after it sets it"),
            "jsr" or "jsl" when next is not null && statement.Operand is not AbsoluteOperandSyntax
                => new($"{written} calls through a pointer, and its `.next` names no routine", "a `.next` naming them carries their exit state here"),
            "jsr" or "jsl" => new($"{written} returns with it unknown", "an `.ensure` after it sets it"),
            _ => new($"{written} makes it unknown", "a `.state` after it says what it is"),
        };
    }

    /// <summary>What one statement does to the state.</summary>
    private FlowState Through(Step step, Step? previous, NextDirectiveSyntax? next, FlowState state, Symbol routine)
    {
        if (step.Statement is StateDirectiveSyntax)
            return Asserted(step, state);
        if (step.Statement is EnsureDirectiveSyntax)
            return Ensured(step, state);
        if (step.Statement is FrameDirectiveSyntax frame)
            return Framed(step, frame, state);
        if (step.IsMarker)
            return Marked(step, state);
        if (step.Statement is not InstructionStatementSyntax statement)
            return state;

        var mnemonic = statement.Mnemonic.Text.ToLowerInvariant();
        var mode = layout.Of(statement, step.On)?.Mode;
        var processor = state.Processor;
        var stack = state.Stack;
        if (mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) is { } register)
        {
            checks.CheckImmediate(
                step, mnemonic, register, processor, register == WidthRegister.A ? state.WhyA : state.WhyIndex, routine);
        }
        Slot(step, mode, stack);
        checks.CheckMemory(step, mnemonic, mode, processor, routine);

        switch (mnemonic)
        {
            case "rep":
            case "sep":
                return state with { Processor = Flags(step, mnemonic == "rep", processor) };

            // `clc` then `xce` enters native mode, and `sec` then `xce` emulation mode. Any
            // other `xce` swaps in a carry nobody knows.
            // D and B are left alone.
            case "xce":
                if (previous is { } clc && Is(clc.Statement, "clc"))
                {
                    return state with
                    {
                        Processor = processor.E switch
                        {
                            ProcessorMode.Emulation => processor with { A = Width.Eight, Index = Width.Eight, E = ProcessorMode.Native },
                            ProcessorMode.Native => processor,
                            _ => processor with { A = Width.Unknown, Index = Width.Unknown, E = ProcessorMode.Native },
                        },
                    };
                }
                return state with
                {
                    Processor = previous is { } sec && Is(sec.Statement, "sec")
                        ? processor with { A = Width.Eight, Index = Width.Eight, E = ProcessorMode.Emulation }
                        : processor with { A = Width.Unknown, Index = Width.Unknown, E = ProcessorMode.Unknown },
                };

            // The direct page is loaded from a constant by `lda #c` then `tcd` with A 16 bits;
            // any other `tcd` loads what nobody knows.
            case "tcd":
                return state with
                {
                    Processor = processor with
                    {
                        D = processor.A == Width.Sixteen && previous is { } load && Loaded(load) is { } page
                            ? StateValue.Of(page & 0xffff)
                            : StateValue.Unknown,
                    },
                };

            // A block move leaves the data bank at its destination.
            case "mvn":
            case "mvp":
                return state with { Processor = processor with { B = MovedTo(step) } };

            case "php":
                return state with { Stack = stack?.Push(new StackEntry(true, processor.A, processor.Index)) };
            // A constant loaded into A just before it is pushed is a value a pull can get back:
            // `lda #c`, `pha`, `plb` loads the data bank.
            case "pha":
                return state with
                {
                    Stack = Bytes(processor.A) is { } bytes && previous is { } loader && Loaded(loader) is { } loaded
                        ? stack?.PushValue(StateValue.Of(loaded & (bytes == 1 ? 0xff : 0xffff)), bytes)
                        : Push(stack, Bytes(processor.A)),
                };
            case "phx":
            case "phy":
                return state with { Stack = Push(stack, Bytes(processor.Index)) };
            case "phb":
                return state with { Stack = stack?.PushValue(processor.B, 1) };
            case "phk":
                return state with { Stack = stack?.PushValue(checks.BankOf(step.Segment), 1) };
            case "phd":
                return state with { Stack = stack?.PushValue(processor.D, 2) };
            case "pea":
                return state with
                {
                    Stack = stack?.PushValue(Constant(step) is { } pushed ? StateValue.Of(pushed & 0xffff) : StateValue.Unknown, 2),
                };
            case "pei":
            case "per":
                return state with { Stack = Push(stack, 2) };
            case "pla":
                return state with { Stack = Pull(stack, Bytes(processor.A)) };
            case "plx":
            case "ply":
                return state with { Stack = Pull(stack, Bytes(processor.Index)) };
            // A pull that finds a value the routine pushed gets it back: a saved D or B, a
            // constant, or the program bank. Any other loads what nobody knows.
            case "plb":
                return new FlowState(processor with { B = stack?.PulledValue(1) ?? StateValue.Unknown }, Pull(stack, 1));
            case "pld":
                return new FlowState(processor with { D = stack?.PulledValue(2) ?? StateValue.Unknown }, Pull(stack, 2));

            // A pull that finds the status register a `php` saved restores the widths saved
            // with it; any other leaves them unknown. The emulation flag is not in it.
            case "plp":
                var restored = stack?.Top is { IsStatus: true } saved
                    ? processor.E == ProcessorMode.Emulation
                        ? processor with { A = Width.Eight, Index = Width.Eight }
                        : processor with { A = saved.A, Index = saved.Index }
                    : processor with { A = Width.Unknown, Index = Width.Unknown };
                return new FlowState(restored, Pull(stack, 1));

            // The stack pointer is somewhere nothing is known of, and what is pushed from
            // here on is tracked on top of it.
            case "txs":
            case "tcs":
                return state with { Stack = AnalysisStack.Unanchored };

            // A return with a `.next` is a jump to the address the routine pushed, and pulls
            // it. Where the `.next` names routines it is a tail call to each of them, checked
            // as a `jmp` to one would be.
            case "rts":
            case "rtl":
                if (next is not null)
                {
                    foreach (var named in Routines(next, step.On))
                        checks.CheckTailCall(step, ".next", named, named.Signature!, processor, routine);
                    return state with { Stack = Pull(stack, mnemonic == "rts" ? 2 : 3) };
                }
                if (routine.Signature is not { HasNoCaller: true })
                    checks.CheckReturn(step, mnemonic, processor, routine);
                return state;

            default:
                return Transferred(step, mnemonic, mode, next, state, routine);
        }
    }

    /// <summary>What a call or a jump does: a call becomes its routine's exit, and a jump to a routine is checked as a tail call.</summary>
    private FlowState Transferred(
        Step step, string mnemonic, AddressingMode? mode, NextDirectiveSyntax? next, FlowState state, Symbol routine)
    {
        var statement = step.Statement;
        var transfer = Transfers.Of(statement, mode);
        var calls = mnemonic is "jsr" or "jsl";
        var target = Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol;

        if (transfer == Transfer.Call)
        {
            checks.CheckMirror(step, mode);
            checks.CheckArguments(step, target, state.Stack, 0);
            return state with { Processor = Called(step, mnemonic, target, state.Processor) };
        }

        // A relative call comes back to the label after it, having pulled what the `per` and
        // any `phk` pushed.
        if (flow.RelativeCallAt(step) is { } relative)
        {
            checks.CheckArguments(step, relative.Routine, state.Stack, relative.Pushed);
            return new FlowState(
                RelativelyCalled(step, mnemonic, relative, state.Processor), Pull(state.Stack, relative.Pushed));
        }

        // Where an indirect call goes is what its `.next` says, and it returns with whatever
        // the routines it names return with. With nothing named, nothing is known after it.
        // With no `.next` at all, that has been reported, and the state is left alone so the
        // one mistake is not reported again wherever the state is used.
        if (calls)
        {
            if (next is null)
                return state;
            var named = Routines(next, step.On).ToList();
            if (named.Count == 0)
                return state with { Processor = ProcessorState.Unknown };
            FlowState? merged = null;
            foreach (var each in named)
                merged = FlowState.Merge(merged, state with { Processor = Called(step, mnemonic, each, state.Processor) });
            return merged!;
        }

        if (transfer is Transfer.Jump or Transfer.Branch && target is { Signature: { } callee })
        {
            checks.CheckMirror(step, mode);
            checks.CheckTailCall(step, mnemonic, target, callee, state.Processor, routine);
        }
        else if (transfer is Transfer.Jump or Transfer.Branch && target is not null && DeclaredElsewhere(target, routine) is { } declared)
        {
            checks.CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", new Signature(declared, declared, false), state.Processor);
        }
        if (next is not null)
        {
            foreach (var named in Routines(next, step.On))
                checks.CheckTailCall(step, ".next", named, named.Signature!, state.Processor, routine);
        }
        return state;
    }

    /// <summary>The routines a <c>.next</c> names; the labels it names are edges of the routine's own.</summary>
    private IEnumerable<Symbol> Routines(NextDirectiveSyntax next, Expansion? on) =>
        flow.Named(next, on).Select(named => named.Symbol).Where(symbol => symbol.Signature is not null);

    /// <summary>Whether a statement calls, directly or through a pointer.</summary>
    private static bool IsCallOrIndirectCall(Step step) =>
        step.Statement is InstructionStatementSyntax instruction
        && Instructions.Facts(instruction.Mnemonic.Text).Calls;

    /// <summary>
    /// A call: the state here must be what the routine expects, and becomes what it returns
    /// with, except for the parts it declares unchanged, which keep what they were.
    /// </summary>
    private ProcessorState Called(Step step, string mnemonic, Symbol? target, ProcessorState state)
    {
        if (target?.Signature is not { } callee)
        {
            checks.CheckCallTarget(step, mnemonic, target);

            // Nothing says what it returns with, and once the call is fixed its signature will;
            // leaving the state alone keeps one mistake to one diagnostic.
            return state;
        }

        if (callee.IsInterrupt)
            return state;
        checks.CheckCall(step, mnemonic, target, callee, state);
        return StateChecks.Exited(callee, state);
    }

    /// <summary>
    /// A call written as <c>per</c> and a branch, checked as <c>jsr</c> or, with a <c>phk</c>
    /// before it, as <c>jsl</c>.
    /// </summary>
    private ProcessorState RelativelyCalled(Step step, string mnemonic, RelativeCall call, ProcessorState state)
    {
        var callee = call.Routine.Signature!;
        if (callee.IsInterrupt)
            return state;
        checks.CheckRelativeCall(step, mnemonic, call, state);
        return StateChecks.Exited(callee, state);
    }

    /// <summary>
    /// What a <c>.state</c> declares at a label inside another routine, which a jump into that
    /// routine has to meet; null when the label is in this routine or declares nothing. Only
    /// the parts it gives are checked. The declaration is read off the label, so the routine
    /// may be in another file.
    /// </summary>
    private ProcessorState? DeclaredElsewhere(Symbol label, Symbol routine)
    {
        if (label is not { Kind: SymbolKind.Label, StateDeclaration: { } declared, Routine: { } owner }
            || owner == routine || owner.IsSiblingOf(routine))
        {
            return null;
        }
        var state = ProcessorState.Unknown;
        foreach (var item in StateItem.Read(declared))
        {
            state = item.Part switch
            {
                StatePart.A => state with { A = item.Width },
                StatePart.Index => state with { Index = item.Width },
                StatePart.E => state with { E = item.Mode },
                StatePart.DirectPage => state with { D = ValueOf(item, model) },
                StatePart.DataBank => state with { B = ValueOf(item, model) },
                _ => state,
            };
        }
        return state;
    }

    /// <summary>
    /// <c>rep #c</c> or <c>sep #c</c>. In native mode the widths it names become known; in
    /// emulation mode the widths are pinned at 8 and nothing changes; where the mode is not
    /// known, a <c>sep</c> still makes them 8, which they are in either mode.
    /// </summary>
    private ProcessorState Flags(Step step, bool reset, ProcessorState state)
    {
        // Emulation mode pins both widths at 8 whatever the operand says, so an operand nt65
        // cannot work out changes nothing there. That is asked first: forgetting the widths and
        // then finding the mode would throw away what the mode already said.
        if (state.E == ProcessorMode.Emulation)
            return state;
        if (Constant(step) is not { } flags)
            return state with { A = Width.Unknown, Index = Width.Unknown };

        var width = reset
            ? state.E == ProcessorMode.Native ? Width.Sixteen : Width.Unknown
            : Width.Eight;
        return state with
        {
            A = (flags & 0x20) != 0 ? width : state.A,
            Index = (flags & 0x10) != 0 ? width : state.Index,
        };
    }

    /// <summary>The value of an instruction's operand, such as the <c>#c</c> of <c>rep #c</c> or the <c>c</c> of <c>pea c</c>, or null when it is not a constant.</summary>
    private long? Constant(Step step) =>
        checks.OperandOf(step) is { } operand && CodeLayout.Expression(operand) is { } expression
            ? model.ValueOf(expression, step.On).AsNumber()
            : null;

    /// <summary>The constant <paramref name="step"/> loads into A, for an <c>lda #c</c>; null for anything else.</summary>
    private long? Loaded(Step step) =>
        Is(step.Statement, "lda") && layout.Of(step.Statement, step.On)?.Mode == AddressingMode.Immediate
            ? Constant(step)
            : null;

    /// <summary>
    /// The destination bank of <c>mvn #src, #dst</c>, which is where it leaves the data bank: a
    /// constant, or <c>^sym</c>, the bank of a symbol whose segment declares one.
    /// </summary>
    private StateValue MovedTo(Step step)
    {
        if (checks.OperandOf(step) is not ImmediateOperandSyntax { SecondValue: { } destination })
            return StateValue.Unknown;
        if (model.ValueOf(destination, step.On).AsNumber() is { } bank and >= 0 and <= 0xff)
            return StateValue.Of(bank);
        return destination is UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Caret, Operand: var named }
            && Targets.Of(model, named, step.On)?.Symbol is { } symbol && checks.SegmentOf(symbol)?.Bank is { } home
                ? StateValue.Of(home)
                : StateValue.Unknown;
    }

    /// <summary>
    /// A <c>.state</c>: each item asserts and sets. Where that part is known and differs it is
    /// an error; where it is not known the item makes it so; an item with <c>?</c> forgets.
    /// </summary>
    private FlowState Asserted(Step step, FlowState state)
    {
        var processor = state.Processor;
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.IsUnchanged || item.Part is StatePart.Distance or StatePart.Inline or StatePart.Arguments
                or StatePart.Interrupt or StatePart.NoReturn or StatePart.Set)
            {
                checks.ReportAt(item.Node, step, $"`{item.Text}` describes a routine rather than a point in it, "
                    + "and belongs in a signature");
                continue;
            }
            switch (item.Part)
            {
                case StatePart.A:
                    processor = processor with { A = Set("A", processor.A, item) };
                    break;
                case StatePart.Index:
                    processor = processor with { Index = Set("X and Y", processor.Index, item) };
                    break;
                case StatePart.E:
                    if (StateChecks.IsKnown(item.Mode) && StateChecks.IsKnown(processor.E) && item.Mode != processor.E)
                    {
                        checks.ReportAt(item.Node, step, $"`.state {item.Text}`, and the processor is in "
                            + $"{StateChecks.Mode(processor.E)} mode here");
                    }
                    processor = processor with { E = item.Mode };
                    break;

                case StatePart.DirectPage:
                    processor = processor with { D = SetValue("D", processor.D, item) };
                    break;
                case StatePart.DataBank:
                    processor = processor with { B = SetValue("B", processor.B, item) };
                    break;
                case StatePart.AllUnknown:
                    processor = new ProcessorState(
                        Width.Unknown, Width.Unknown, ProcessorMode.Unknown, StateValue.Unknown, StateValue.Unknown);
                    break;
                default:
                    break;
            }
        }

        // Emulation mode pins both widths at 8 bits, so saying so says that too.
        if (processor.E == ProcessorMode.Emulation)
        {
            if (processor.A == Width.Sixteen || processor.Index == Width.Sixteen)
                checks.Report(step, "a 16-bit width cannot hold in emulation mode, where both widths are 8 bits");
            processor = processor with { A = Width.Eight, Index = Width.Eight };
        }
        return state with { Processor = processor };

        StateValue SetValue(string register, StateValue here, StateItem item)
        {
            if (item.Expression is not { } expression)
                return StateValue.Unknown;
            if (model.ValueOf(expression, step.On).AsNumber() is not { } value)
            {
                checks.ReportAt(expression, step, $"`.state {item.Text}` needs a constant: the analysis follows {register} by value");
                return StateValue.Unknown;
            }
            if (value < 0 || value > (register == "D" ? 0xffff : 0xff))
            {
                checks.ReportAt(expression, step, $"`.state {item.Text}` is out of range: "
                    + (register == "D" ? "the direct page is a 16-bit address" : "a bank is one byte"));
                return StateValue.Unknown;
            }
            if (here.IsKnown && here.Value != value)
            {
                checks.ReportAt(item.Node, step,
                    $"`.state {item.Text}`, and {register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} here");
            }
            return StateValue.Of(value);
        }

        Width Set(string register, Width here, StateItem item)
        {
            if (StateChecks.IsKnown(item.Width) && StateChecks.IsKnown(here) && item.Width != here)
            {
                checks.ReportAt(item.Node, step, $"`.state {item.Text}`, and {register} "
                    + $"{(register == "A" ? "is" : "are")} {StateChecks.Spell(here)} here");
            }
            return item.Width;
        }
    }

    /// <summary>
    /// <c>.ensure a16, i8</c>: the widths it names hold after it, whatever it writes to make
    /// them. A 16-bit width needs native mode, and only native mode known here says it is.
    /// </summary>
    private FlowState Ensured(Step step, FlowState state)
    {
        var processor = state.Processor;
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part is not (StatePart.A or StatePart.Index) || !StateChecks.IsKnown(item.Width))
            {
                checks.ReportAt(item.Node, step, "`.ensure` makes widths hold, and takes `a8`, `a16`, `i8` and `i16`: "
                    + $"`{item.Text}` is not one of them");
                continue;
            }
            if (item.Width == Width.Sixteen && processor.E != ProcessorMode.Native)
            {
                checks.ReportAt(item.Node, step, $"`.ensure {item.Text}` needs native mode, and "
                    + (processor.E == ProcessorMode.Emulation
                        ? "the processor is in emulation mode here, where both widths are 8 bits"
                        : "the mode is not known here"));
            }
            // Emulation mode pins both widths at 8, whatever is written to change them.
            if (processor.E == ProcessorMode.Emulation)
                continue;
            processor = item.Part == StatePart.A
                ? processor with { A = item.Width }
                : processor with { Index = item.Width };
        }
        return state with { Processor = processor };
    }

    /// <summary>
    /// <c>.frame name: T</c>: the top <c>.sizeof(T)</c> bytes of the analysis stack become the
    /// frame. Where the stack is not known, as after <c>tcs</c>, it becomes those bytes with
    /// nothing known beneath them.
    /// </summary>
    private FlowState Framed(Step step, FrameDirectiveSyntax directive, FlowState state)
    {
        if (model.SymbolAt(directive.Name) is not { Kind: SymbolKind.Frame } frame)
            return state;
        if (directive.Type is not { } type
            || model.SymbolOf(type) is not { IsLayout: true, Size: { } size })
        {
            checks.Report(step, $"`.frame {frame.DisplayName}` is laid out as a struct or a union, whose size says how many bytes it names");
            return state;
        }
        if (state.Stack is not { } stack)
            return state with { Stack = AnalysisStack.OnlyFrame(frame, (int)size) };
        if (stack.Framed(frame, (int)size) is { } framed)
            return state with { Stack = framed };
        checks.Report(step, $"`{frame.DisplayName}` is {size} bytes, and only {stack.Depth} are pushed here");
        return state;
    }

    /// <summary>
    /// A frame's member named in an operand, which is only a place on the stack: it is a
    /// stack-relative operand, and its offset counts every byte pushed since the frame.
    /// </summary>
    private void Slot(Step step, AddressingMode? mode, AnalysisStack? stack)
    {
        if ((step.Statement as InstructionStatementSyntax)?.Operand is not { } operand)
            return;
        foreach (var name in operand.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            if (name.GlobalToken is not null || name.Names is not [var first, ..]
                || model.SymbolAt(first) is not { Kind: SymbolKind.Frame } frame)
            {
                continue;
            }
            var written = name.GetText().Trim();
            if (mode is not (AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
                || name.Parent is not OperandSyntax)
            {
                checks.ReportAt(name, step, $"`{written}` is a place on the stack, and is named only on its own as a "
                    + $"stack-relative operand: `{written},s`");
                continue;
            }
            if (stack is null)
            {
                checks.ReportAt(name, step, $"`{written}` is counted from the stack pointer, and how much is pushed is not known here");
                continue;
            }
            if (stack.Above(frame) is not { } above)
            {
                checks.ReportAt(name, step, $"`{written}` is in `{frame.DisplayName}`, which is no longer on the stack here");
                continue;
            }
            var size = frame.TypeExpression is { } type ? model.SymbolOf(type)?.Size ?? 0 : 0;
            var offset = name.Names.Length > 1 ? model.ValueOf(name, step.On).AsNumber() ?? 0 : 0;

            // The frame's lowest byte is its last member's, and `1,s` is the byte on top.
            if (checks.Final)
                slots[(step.Statement.Position, step.On)] = (int)(above - size + 1 + offset + 1);
        }
    }

    /// <summary>
    /// Where an expansion of a macro with a state signature, or a block spliced into one,
    /// starts or ends. A call is checked the way <c>jsr</c> is: the state must match the
    /// entry, the body starts from it, its end must match the exit, and the state after the
    /// call is the exit with its <c>*</c> items kept from the call. A block given to such a
    /// macro has to leave the state as it found it.
    /// </summary>
    private FlowState Marked(Step step, FlowState state)
    {
        var key = (step.Statement.Position, step.On);
        var processor = state.Processor;
        if (step.Statement is BlockSpliceSyntax)
        {
            if (!step.Closes)
            {
                started[key] = processor;
            }
            else if (started.TryGetValue(key, out var before) && before != processor
                && step.On?.NearestCall is { } call && model.MacroAt(call) is { } owner)
            {
                checks.Report(step, $"the block given to `{owner.DisplayName}!` has to leave the state as it found it: "
                    + $"it starts with `{before}` and ends with `{processor}`");
            }
            return state;
        }

        if (step.Statement is not MacroCallSyntax expanded || model.MacroAt(expanded) is not { MacroSignature: { } signature } macro)
            return state;
        var name = macro.DisplayName + "!";
        if (!step.Closes)
        {
            started[key] = processor;
            checks.CheckEntry(step, $"`{name}`", signature, processor);
            return state with { Processor = signature.Entry };
        }
        checks.CheckExit(step, "", "at the end of its body", signature.Exit, processor, name);
        return state with
        {
            Processor = StateChecks.Exited(signature, started.TryGetValue(key, out var at) ? at : ProcessorState.Unknown),
        };
    }
}
