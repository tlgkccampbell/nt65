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
/// reports, and an unknown value is an error only where it is used.
/// </para>
/// </summary>
public sealed class StateAnalysis
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly Dictionary<(int Position, Expansion? On), FlowState> reaching = [];
    private readonly Dictionary<(int Position, Expansion? On), int> slots = [];

    // The state at the start of each expansion of a macro with a signature, and of each block
    // spliced into one, which is what the end of it is checked against and hands back.
    private readonly Dictionary<(int Position, Expansion? On), ProcessorState> started = [];
    private readonly List<Diagnostic> diagnostics = [];

    // The banks an absolute constant address in each range may be reached from.
    private readonly IReadOnlyList<Project.AccessRange> ranges;

    // Whether the walk is the last one, over converged states, which is the only one that
    // reports and records.
    private bool final;

    private StateAnalysis(SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange> ranges)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        this.ranges = ranges;
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
        analysis.final = true;
        analysis.CheckOutsideRoutines();
        analysis.Diagnostics = Norristown.Diagnostics.Ordered(
            analysis.diagnostics.DistinctBy(d => (d.Span, d.Message)));
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

    /// <summary>What a routine's state is when it is entered: its declared entry, with nothing pushed.</summary>
    private static FlowState Entry(Signature signature, Symbol routine) => new(signature.Entry, AnalysisStack.Empty)
    {
        WhyA = signature.Entry.A == Width.Unknown ? EntryCause(routine, "a?") : null,
        WhyIndex = signature.Entry.Index == Width.Unknown ? EntryCause(routine, "i?") : null,
    };

    private static WidthCause EntryCause(Symbol routine, string item) =>
        new($"`{routine.DisplayName}` says `{item}` at entry", "an `.ensure` sets it");

    /// <summary>The register an immediate's width comes from, as a message names it.</summary>
    private static string Spell(WidthRegister register) => register == WidthRegister.A ? "A" : "X and Y";

    /// <summary>A known width as a message says it.</summary>
    private static string Spell(Width width) => width == Width.Sixteen ? "16-bit" : "8-bit";

    /// <summary>How many bytes a push or pull of a register this wide moves, or null when that is not known.</summary>
    private static int? Bytes(Width width) => width switch
    {
        Width.Eight => 1,
        Width.Sixteen => 2,
        _ => null,
    };

    private static bool IsKnown(Width width) => width is Width.Eight or Width.Sixteen;

    private static bool IsKnown(ProcessorMode mode) => mode is ProcessorMode.Native or ProcessorMode.Emulation;

    private static bool Is(SyntaxNode statement, string mnemonic) =>
        statement.Kind == SyntaxKind.InstructionStatement && statement.ChildTokens.Length > 0
        && statement.ChildTokens[0].Text.Equals(mnemonic, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a routine hands back: its exit, with the parts it declares unchanged kept from <paramref name="state"/>.</summary>
    private static ProcessorState Exited(Signature callee, ProcessorState state) => new(
        callee.Exit.A == Width.Unchanged ? state.A : callee.Exit.A,
        callee.Exit.Index == Width.Unchanged ? state.Index : callee.Exit.Index,
        callee.Exit.E == ProcessorMode.Unchanged ? state.E : callee.Exit.E,
        callee.Exit.D.Kind == StateValueKind.Unchanged ? state.D : callee.Exit.D,
        callee.Exit.B.Kind == StateValueKind.Unchanged ? state.B : callee.Exit.B);

    /// <summary>What a <c>dp = e</c> or <c>dbr = e</c> item says, or unknown for <c>dp?</c> and for a value nt65 cannot work out.</summary>
    private static StateValue ValueOf(StateItem item, SemanticModel model) =>
        item.Expression is { } expression && model.ValueOf(expression).AsNumber() is { } value
            ? StateValue.Of(value)
            : StateValue.Unknown;

    private static string Mode(ProcessorMode mode) => mode == ProcessorMode.Native ? "native" : "emulation";

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

        // A label a `.state` declares is an entry point in its own right. One that some path
        // already reaches is checked against that path; one nothing reaches starts from what
        // the directive says, over a state otherwise unknown. A routine that promises to hand
        // back D or B unchanged is taken to have left them alone on the way to the label too,
        // so only a routine that declares them needs its labels to.
        foreach (var block in blocks)
        {
            if (block.IsDeclared && reached[block.Index] is null)
            {
                var unknown = ProcessorState.Unknown;
                if (signature.Entry.D.Kind == StateValueKind.Unchanged)
                    unknown = unknown with { D = StateValue.Unchanged };
                if (signature.Entry.B.Kind == StateValueKind.Unchanged)
                    unknown = unknown with { B = StateValue.Unchanged };
                reached[block.Index] = new FlowState(unknown, null);
                pending.Add(block.Index);
                Settle();
            }
        }

        final = true;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is { } state)
                Walk(block, state, region);
            else
                Unreached(block, region);
        }
        final = false;
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

    /// <summary>The state through one block, from the state that reaches it.</summary>
    private FlowState Walk(BasicBlock block, FlowState state, FlowRegion region)
    {
        var routine = region.Routine;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            if (final && !step.Closes)
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
    private FlowState Explained(Step step, SyntaxNode? next, FlowState before, FlowState after) => after with
    {
        WhyA = Why(step, next, before.Processor.E, before.Processor.A, after.Processor.A, before.WhyA),
        WhyIndex = Why(step, next, before.Processor.E, before.Processor.Index, after.Processor.Index, before.WhyIndex),
    };

    private WidthCause? Why(
        Step step, SyntaxNode? next, ProcessorMode mode, Width before, Width after, WidthCause? carried)
    {
        if (after != Width.Unknown)
            return null;
        if (before == Width.Unknown)
            return carried;

        var statement = step.Statement;
        if (statement.Kind == SyntaxKind.StateDirective)
            return new("a `.state` says so", "the `.state` can say what it is");
        if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
            return null;
        var written = $"`{statement.GetText().Trim()}`";
        return statement.ChildTokens[0].Text.ToLowerInvariant() switch
        {
            "plp" => new($"{written} pulls a status that no `php` in this routine pushed", "an `.ensure` after it sets it"),
            "xce" => new($"{written} follows neither `clc` nor `sec`", "a `.state` after it says what it is"),
            "rep" when mode != ProcessorMode.Native && Constant(step) is not null
                => new($"{written} widens nothing in emulation mode, and the mode is not known", "a `.state` before it says which mode it is"),
            "rep" or "sep" => new($"{written} changes flags nt65 cannot work out", "an `.ensure` after it sets it"),
            "jsr" or "jsl" when next is not null && statement.ChildNodes.FirstOrDefault()?.Kind is not SyntaxKind.AbsoluteOperand
                => new($"{written} calls through a pointer, and its `.next` names no routine", "a `.next` naming them carries their exit state here"),
            "jsr" or "jsl" => new($"{written} returns with it unknown", "an `.ensure` after it sets it"),
            _ => new($"{written} makes it unknown", "a `.state` after it says what it is"),
        };
    }

    /// <summary>What one statement does to the state.</summary>
    private FlowState Through(Step step, Step? previous, SyntaxNode? next, FlowState state, Symbol routine)
    {
        var statement = step.Statement;
        if (statement.Kind == SyntaxKind.StateDirective)
            return Asserted(step, state);
        if (statement.Kind == SyntaxKind.EnsureDirective)
            return Ensured(step, state);
        if (statement.Kind == SyntaxKind.FrameDirective)
            return Framed(step, state);
        if (step.IsMarker)
            return Marked(step, state);
        if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
            return state;

        var mnemonic = statement.ChildTokens[0].Text.ToLowerInvariant();
        var mode = layout.Of(statement, step.On)?.Mode;
        var processor = state.Processor;
        var stack = state.Stack;
        if (mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) is { } register)
            CheckImmediate(step, mnemonic, register, processor, register == WidthRegister.A ? state.WhyA : state.WhyIndex, routine);
        Slot(step, mode, stack);
        CheckMemory(step, mnemonic, mode, processor, routine);

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
                return state with { Stack = stack?.PushValue(BankOf(step.Segment), 1) };
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

            case "txs":
            case "tcs":
                return state with { Stack = null };

            // A return with a `.next` is a jump to the address the routine pushed, and pulls it.
            case "rts":
            case "rtl":
                if (next is not null)
                    return state with { Stack = Pull(stack, mnemonic == "rts" ? 2 : 3) };
                CheckReturn(step, mnemonic, processor, routine);
                return state;

            default:
                return Transferred(step, mnemonic, mode, next, state, routine);
        }
    }

    /// <summary>What a call or a jump does: a call becomes its routine's exit, and a jump to a routine is checked as a tail call.</summary>
    private FlowState Transferred(
        Step step, string mnemonic, AddressingMode? mode, SyntaxNode? next, FlowState state, Symbol routine)
    {
        var statement = step.Statement;
        var transfer = Transfers.Of(statement, mode);
        var calls = mnemonic is "jsr" or "jsl";
        var target = Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol;

        if (transfer == Transfer.Call)
            return state with { Processor = Called(step, mnemonic, target, state.Processor) };

        // A relative call comes back to the label after it, having pulled what the `per` and
        // any `phk` pushed.
        if (flow.RelativeCallAt(step) is { } relative)
        {
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
            CheckTailCall(step, mnemonic, target, callee, state.Processor, routine);
        else if (transfer is Transfer.Jump or Transfer.Branch && target is not null && DeclaredElsewhere(target, routine) is { } declared)
            CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", new Signature(declared, declared, false), state.Processor);
        if (next is not null)
        {
            foreach (var named in Routines(next, step.On))
                CheckTailCall(step, ".next", named, named.Signature!, state.Processor, routine);
        }
        return state;
    }

    /// <summary>The routines a <c>.next</c> names; the labels it names are edges of the routine's own.</summary>
    private IEnumerable<Symbol> Routines(SyntaxNode next, Expansion? on) =>
        flow.Named(next, on).Select(named => named.Symbol).Where(symbol => symbol.Signature is not null);

    /// <summary>Whether a statement calls, directly or through a pointer.</summary>
    private bool IsCallOrIndirectCall(Step step) =>
        step.Statement.Kind == SyntaxKind.InstructionStatement
        && (Is(step.Statement, "jsr") || Is(step.Statement, "jsl"));

    /// <summary>
    /// A call: the state here must be what the routine expects, and becomes what it returns
    /// with, except for the parts it declares unchanged, which keep what they were.
    /// </summary>
    private ProcessorState Called(Step step, string mnemonic, Symbol? target, ProcessorState state)
    {
        if (target?.Signature is not { } callee)
        {
            Report(step, target is null
                ? $"`{mnemonic}` needs a routine to call: on the 65816 a call's target is a proc, an extern "
                    + "proc or a `proc(...)` import, whose signature says what state it takes"
                : $"`{target.DisplayName}` is not a routine: on the 65816 a call's target is a proc, an "
                    + "extern proc or a `proc(...)` import, whose signature says what state it takes");

            // Nothing says what it returns with, and once the call is fixed its signature will;
            // leaving the state alone keeps one mistake to one diagnostic.
            return state;
        }

        if (mnemonic == "jsr" && callee.IsFar)
            Report(step, $"`{target.DisplayName}` is far, and is called with `jsl`");
        else if (mnemonic == "jsl" && !callee.IsFar)
            Report(step, $"`{target.DisplayName}` is near, and is called with `jsr`");
        CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", callee, state);
        return Exited(callee, state);
    }

    /// <summary>
    /// A call written as <c>per</c> and a branch, checked as <c>jsr</c> or, with a <c>phk</c>
    /// before it, as <c>jsl</c>.
    /// </summary>
    private ProcessorState RelativelyCalled(Step step, string mnemonic, RelativeCall call, ProcessorState state)
    {
        var target = call.Routine;
        var callee = target.Signature!;
        if (callee.IsFar && !call.IsFar)
            Report(step, $"`{target.DisplayName}` is far, and a relative call to it pushes the bank with `phk` before the `per`");
        else if (!callee.IsFar && call.IsFar)
            Report(step, $"`{target.DisplayName}` is near, and a relative call to it pushes no bank: the `phk` is one byte too many");
        CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", callee, state);
        return Exited(callee, state);
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
            || owner == routine)
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
    /// A jump to a routine's entry. The routine returns to this routine's caller, so it has to
    /// take the state here, return the way this routine returns, and hand back what this
    /// routine promises.
    /// </summary>
    private void CheckTailCall(
        Step step, string mnemonic, Symbol target, Signature callee, ProcessorState state, Symbol routine)
    {
        var own = routine.Signature ?? Signature.Default;
        var what = mnemonic == ".next"
            ? $"`.next {target.DisplayName}`"
            : $"`{mnemonic} {target.DisplayName}`";
        if (mnemonic == "jml" && !callee.IsFar)
            Report(step, $"`{target.DisplayName}` is near: a jump to it is `jmp {target.DisplayName}`");
        else if (mnemonic is not ("jml" or ".next") && callee.IsFar)
            Report(step, $"`{target.DisplayName}` is far: a jump to it is `jml {target.DisplayName}`");
        CheckEntry(step, what, callee, state);

        if (callee.IsFar != own.IsFar)
        {
            Report(step, $"{what} is a tail call, and `{target.DisplayName}` is {callee.Distance} while "
                + $"`{routine.DisplayName}` is {own.Distance}: it would return to the caller the wrong way");
        }
        CheckExit(step, $"{what} is a tail call:", $"when `{target.DisplayName}` returns",
            own.Exit, Exited(callee, state), routine.DisplayName);
    }

    /// <summary>That the state here is what a routine's entry declares.</summary>
    private void CheckEntry(Step step, string what, Signature callee, ProcessorState state)
    {
        Width("a", "A", callee.Entry.A, state.A);
        Width("i", "X and Y", callee.Entry.Index, state.Index);
        if (IsKnown(callee.Entry.E) && callee.Entry.E != state.E)
        {
            Report(step, $"{what} needs `{ProcessorState.Spell(callee.Entry.E)}`, and "
                + (IsKnown(state.E) ? $"the processor is in {Mode(state.E)} here" : "the mode is not known here"));
        }
        Value("dp", "D", callee.Entry.D, state.D);
        Value("dbr", "B", callee.Entry.B, state.B);

        void Value(string item, string register, StateValue needed, StateValue here)
        {
            if (!needed.IsKnown || needed == here)
                return;
            Report(step, $"{what} needs `{needed.Spell(item)}`, and "
                + (here.IsKnown ? $"{register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} here" : $"{register} is not known here"));
        }

        void Width(string item, string register, Width needed, Width here)
        {
            if (!IsKnown(needed) || needed == here)
                return;
            Report(step, $"{what} needs `{ProcessorState.Spell(item, needed)}`, and "
                + (IsKnown(here) ? $"{register} {(register == "A" ? "is" : "are")} {Spell(here)} here"
                    : $"the width of {register} is not known here"));
        }
    }

    /// <summary>That <paramref name="state"/> is what the routine or macro <paramref name="name"/> declares it returns with.</summary>
    /// <remarks><paramref name="where"/> says where <paramref name="state"/> holds, as the message puts it.</remarks>
    private void CheckExit(
        Step step, string what, string where, ProcessorState exit, ProcessorState state, string name)
    {
        var lead = what.Length == 0 ? "" : what + " ";
        Part("a", "A", exit.A, state.A);
        Part("i", "X and Y", exit.Index, state.Index);
        if (exit.E == ProcessorMode.Unchanged && state.E != ProcessorMode.Unchanged)
        {
            Report(step, $"{lead}`{name}` says `e*`, so the mode must be what it was on entry, "
                + $"and {where} it may not be");
        }
        else if (IsKnown(exit.E) && exit.E != state.E)
        {
            Report(step, $"{lead}`{name}` returns in {Mode(exit.E)} mode, and "
                + (IsKnown(state.E)
                    ? $"the processor is in {Mode(state.E)} mode {where}"
                    : $"the mode is not known {where}"));
        }

        Value("dp", "D", exit.D, state.D);
        Value("dbr", "B", exit.B, state.B);

        void Value(string item, string register, StateValue declared, StateValue here)
        {
            if (declared.Kind == StateValueKind.Unchanged && here.Kind != StateValueKind.Unchanged)
            {
                Report(step, $"{lead}`{name}` says `{item}*`, so {register} must be what it was on entry, "
                    + $"and {where} it may not be");
            }
            else if (declared.IsKnown && declared != here)
            {
                Report(step, $"{lead}`{name}` returns with `{declared.Spell(item)}`, and "
                    + (here.IsKnown ? $"{register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} {where}" : $"{register} is not known {where}"));
            }
        }

        void Part(string item, string register, Width declared, Width here)
        {
            if (declared == Width.Unchanged && here != Width.Unchanged)
            {
                Report(step, $"{lead}`{name}` says `{item}*`, so {register} must be as wide as it "
                    + $"was on entry, and {where} it may not be");
            }
            else if (IsKnown(declared) && declared != here)
            {
                Report(step, $"{lead}`{name}` returns with `{ProcessorState.Spell(item, declared)}`, and "
                    + (IsKnown(here) ? $"{register} {(register == "A" ? "is" : "are")} {Spell(here)} {where}"
                        : $"the width of {register} is not known {where}"));
            }
        }
    }

    /// <summary>A return: it has to leave the way the routine is called, in the state it declares.</summary>
    private void CheckReturn(Step step, string mnemonic, ProcessorState state, Symbol routine)
    {
        var signature = routine.Signature ?? Signature.Default;
        if (mnemonic == "rts" && signature.IsFar)
            Report(step, $"`{routine.DisplayName}` is far, and returns with `rtl`");
        else if (mnemonic == "rtl" && !signature.IsFar)
            Report(step, $"`{routine.DisplayName}` is near, and returns with `rts`");
        CheckExit(step, $"`{mnemonic}`:", "here", signature.Exit, state, routine.DisplayName);
    }

    /// <summary>
    /// A width-dependent immediate, which ca65 sizes from the width it is told. The analysis
    /// is what tells it, so the width has to be known here.
    /// </summary>
    private void CheckImmediate(
        Step step, string mnemonic, WidthRegister register, ProcessorState state, WidthCause? why, Symbol routine)
    {
        var width = state.Of(register);
        var item = register == WidthRegister.A ? "a" : "i";
        if (width == Width.Unchanged)
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and `{Owner(step, routine)}` says "
                + $"`{item}*`, which assumes nothing about it");
        }
        else if (!IsKnown(width))
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and it is not known here"
                + (why is null ? ": a `.state` says what it is" : $", because {why.Reason}: {why.Fix}"));
        }
        else if (width == Width.Sixteen && state.E == ProcessorMode.Emulation)
        {
            Report(step, $"`{mnemonic} #` would be 16 bits in emulation mode, where both widths are 8");
        }
    }

    /// <summary>
    /// A block no path from the routine's entry reaches, and no <c>.state</c> declares. Its
    /// immediates cannot be sized, because nothing says how wide anything is there.
    /// </summary>
    private void Unreached(BasicBlock block, FlowRegion region)
    {
        foreach (var step in block.Steps)
        {
            var statement = step.Statement;
            if (statement.Kind == SyntaxKind.InstructionStatement && OperandOf(step) is { } operand
                && CodeLayout.ThroughDirectPage(operand))
            {
                Report(step, $"`d:` is reached through the direct page, and no path from `{region.Routine.DisplayName}`'s "
                    + "entry reaches it. A `.state` after its label declares what the state is there");
                continue;
            }
            if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0
                || layout.Of(statement, step.On)?.Mode != AddressingMode.Immediate
                || Instructions.SizedBy(statement.ChildTokens[0].Text) is not { } register)
            {
                continue;
            }
            Report(step, $"`{statement.ChildTokens[0].Text.ToLowerInvariant()} #` needs the width of "
                + $"{Spell(register)}, and no path from `{region.Routine.DisplayName}`'s entry reaches it. "
                + "A `.state` after its label declares what the state is there");
        }
    }

    /// <summary>
    /// <c>rep #c</c> or <c>sep #c</c>. In native mode the widths it names become known; in
    /// emulation mode the widths are pinned at 8 and nothing changes; where the mode is not
    /// known, a <c>sep</c> still makes them 8, which they are in either mode.
    /// </summary>
    private ProcessorState Flags(Step step, bool reset, ProcessorState state)
    {
        if (Constant(step) is not { } flags)
            return state with { A = Width.Unknown, Index = Width.Unknown };
        if (state.E == ProcessorMode.Emulation)
            return state;

        var width = reset
            ? state.E == ProcessorMode.Native ? Width.Sixteen : Width.Unknown
            : Width.Eight;
        return state with
        {
            A = (flags & 0x20) != 0 ? width : state.A,
            Index = (flags & 0x10) != 0 ? width : state.Index,
        };
    }

    /// <summary>The operand an instruction has on this writing of it: what a call gave, where the body names an <c>operand</c> parameter.</summary>
    private SyntaxNode? OperandOf(Step step)
    {
        var written = step.Statement.ChildNodes.FirstOrDefault();
        return Operands.Substituted(model, written, step.On)?.Operand ?? written;
    }

    /// <summary>The value of an instruction's operand, such as the <c>#c</c> of <c>rep #c</c> or the <c>c</c> of <c>pea c</c>, or null when it is not a constant.</summary>
    private long? Constant(Step step) =>
        OperandOf(step)?.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix) is { } expression
            ? model.ValueOf(expression, step.On).AsNumber()
            : null;

    /// <summary>The constant <paramref name="step"/> loads into A, for an <c>lda #c</c>; null for anything else.</summary>
    private long? Loaded(Step step) =>
        Is(step.Statement, "lda") && layout.Of(step.Statement, step.On)?.Mode == AddressingMode.Immediate
            ? Constant(step)
            : null;

    /// <summary>The destination bank of <c>mvn #src, #dst</c>, which is where it leaves the data bank.</summary>
    private StateValue MovedTo(Step step) =>
        OperandOf(step) is { Kind: SyntaxKind.ImmediateOperand, ChildNodes: [_, var destination, ..] }
        && model.ValueOf(destination, step.On).AsNumber() is { } bank and >= 0 and <= 0xff
            ? StateValue.Of(bank)
            : StateValue.Unknown;

    /// <summary>The bank a segment declares it lives in, which is the program bank for code in it.</summary>
    private StateValue BankOf(string segment) =>
        model.Segments.Find(segment)?.Bank is { } bank ? StateValue.Of(bank) : StateValue.Unknown;

    /// <summary>
    /// What memory an operand reaches through the direct page or the data bank, checked
    /// against what the segments and the project's <c>ranges</c> declare. Where either side is
    /// not declared or not known, nothing is reported: the checks are opt-in by declaration.
    /// </summary>
    private void CheckMemory(Step step, string mnemonic, AddressingMode? mode, ProcessorState state, Symbol routine)
    {
        if (mode is not { } chosen || OperandOf(step) is not { } operand
            || operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix) is not { } expression)
        {
            return;
        }

        if (Instructions.Width(chosen) == AddressSize.ZeroPage)
        {
            if (CodeLayout.ThroughDirectPage(operand))
            {
                CheckThroughDirectPage(step, expression, state, routine);
                return;
            }
            if (!state.D.IsKnown)
                return;
            foreach (var symbol in AddressSymbols.In(model, expression, step.On))
            {
                if (SegmentOf(symbol) is { DirectPage: { } page } segment && page != state.D.Value)
                {
                    Report(step, $"`{symbol.DisplayName}` is in \"{segment.Name}\", which is reached through the direct "
                        + $"page at {StateValue.Hex(page, 4)}, and D is {StateValue.Hex(state.D.Value, 4)} here");
                }
            }
            return;
        }

        // A near transfer stays in the program bank, so a target in a segment in another bank
        // is out of its reach.
        if (mnemonic != "per" && (chosen is AddressingMode.Relative or AddressingMode.RelativeLong
            || (chosen == AddressingMode.Absolute && mnemonic is "jmp" or "jsr")))
        {
            CheckNearBank(step, mnemonic, chosen);
            return;
        }

        // Only an absolute operand of an instruction that reads or writes data uses B: a long
        // operand names its bank, `jmp` and `jsr` use the program bank, and `pea` and `per`
        // reach no memory at all.
        if (chosen is not (AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY)
            || mnemonic is "jmp" or "jsr" or "pea" or "per" || !state.B.IsKnown)
        {
            return;
        }
        var bank = state.B.Value;
        foreach (var symbol in AddressSymbols.In(model, expression, step.On))
        {
            if (SegmentOf(symbol) is { Bank: { } declared } segment && declared != bank)
            {
                Report(step, $"`{symbol.DisplayName}` is in \"{segment.Name}\", which is in bank {StateValue.Hex(declared, 2)}, "
                    + $"and B is {StateValue.Hex(bank, 2)} here");
            }
        }
        if (model.ValueOf(expression, step.On).AsNumber() is { } address
            && ranges.FirstOrDefault(range => range.Covers(address)) is { } covering && !covering.Permits(bank))
        {
            Report(step, $"{StateValue.Hex(address, 4)} is reached only from banks {covering.SpellBanks()}, and B is "
                + $"{StateValue.Hex(bank, 2)} here");
        }
    }

    /// <summary>
    /// <c>jsr</c>, <c>jmp</c> or a branch to a label or a routine whose segment declares a bank
    /// other than the one the code around it declares. Both sides have to declare a bank.
    /// </summary>
    private void CheckNearBank(Step step, string mnemonic, AddressingMode mode)
    {
        if (BankOf(step.Segment) is not { IsKnown: true } here
            || Targets.Of(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Symbol is not { } target
            || SegmentOf(target) is not { Bank: { } there } segment || there == here.Value)
        {
            return;
        }
        var fix = mnemonic == "jsr" ? "jsl" : "jml";
        Report(step, $"`{mnemonic}` stays in bank {StateValue.Hex(here.Value, 2)}, and `{target.DisplayName}` is in "
            + $"\"{segment.Name}\", in bank {StateValue.Hex(there, 2)}: `{fix}` reaches it");
    }

    /// <summary>
    /// <c>d:</c> on a constant address, which reaches it through the direct page: D has to be
    /// known here, and the address in the page it starts.
    /// </summary>
    private void CheckThroughDirectPage(Step step, SyntaxNode expression, ProcessorState state, Symbol routine)
    {
        if (model.ValueOf(expression, step.On).AsNumber() is not { } address)
            return;
        var what = $"`d:{StateValue.Hex(address, 4)}` is reached through the direct page";
        if (state.D.Kind == StateValueKind.Unchanged)
        {
            Report(step, $"{what}, and `{Owner(step, routine)}` says `dp*`, which assumes nothing about D");
        }
        else if (!state.D.IsKnown)
        {
            Report(step, $"{what}, and D is not known here: a `.state dp = ...` says what it is");
        }
        else if (address < state.D.Value || address > state.D.Value + 0xff)
        {
            Report(step, $"{what} at {StateValue.Hex(state.D.Value, 4)}, which reaches only "
                + $"{StateValue.Hex(state.D.Value, 4)} to {StateValue.Hex(state.D.Value + 0xff, 4)}");
        }
    }

    /// <summary>The segment a placed symbol is in, as the program's table declares it.</summary>
    private Segment? SegmentOf(Symbol symbol) => model.Segments.Find(symbol.Segment ?? SegmentTable.DefaultSegment);

    /// <summary>
    /// A <c>.state</c>: each item asserts and sets. Where that part is known and differs it is
    /// an error; where it is not known the item makes it so; an item with <c>?</c> forgets.
    /// </summary>
    private FlowState Asserted(Step step, FlowState state)
    {
        var processor = state.Processor;
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.IsUnchanged || item.Part is StatePart.Distance or StatePart.Inline)
            {
                ReportAt(item.Node, step, $"`{item.Text}` describes a routine rather than a point in it, "
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
                    if (IsKnown(item.Mode) && IsKnown(processor.E) && item.Mode != processor.E)
                    {
                        ReportAt(item.Node, step, $"`.state {item.Text}`, and the processor is in "
                            + $"{Mode(processor.E)} mode here");
                    }
                    processor = processor with { E = item.Mode };
                    break;

                case StatePart.DirectPage:
                    processor = processor with { D = SetValue("D", processor.D, item) };
                    break;
                case StatePart.DataBank:
                    processor = processor with { B = SetValue("B", processor.B, item) };
                    break;
                default:
                    break;
            }
        }

        // Emulation mode pins both widths at 8 bits, so saying so says that too.
        if (processor.E == ProcessorMode.Emulation)
        {
            if (processor.A == Width.Sixteen || processor.Index == Width.Sixteen)
                Report(step, "a 16-bit width cannot hold in emulation mode, where both widths are 8 bits");
            processor = processor with { A = Width.Eight, Index = Width.Eight };
        }
        return state with { Processor = processor };

        StateValue SetValue(string register, StateValue here, StateItem item)
        {
            if (item.Expression is not { } expression)
                return StateValue.Unknown;
            if (model.ValueOf(expression, step.On).AsNumber() is not { } value)
            {
                ReportAt(expression, step, $"`.state {item.Text}` needs a constant: the analysis follows {register} by value");
                return StateValue.Unknown;
            }
            if (value < 0 || value > (register == "D" ? 0xffff : 0xff))
            {
                ReportAt(expression, step, $"`.state {item.Text}` is out of range: "
                    + (register == "D" ? "the direct page is a 16-bit address" : "a bank is one byte"));
                return StateValue.Unknown;
            }
            if (here.IsKnown && here.Value != value)
                ReportAt(item.Node, step, $"`.state {item.Text}`, and {register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} here");
            return StateValue.Of(value);
        }

        Width Set(string register, Width here, StateItem item)
        {
            if (IsKnown(item.Width) && IsKnown(here) && item.Width != here)
            {
                ReportAt(item.Node, step, $"`.state {item.Text}`, and {register} "
                    + $"{(register == "A" ? "is" : "are")} {Spell(here)} here");
            }
            return item.Width;
        }
    }

    /// <summary>
    /// What says a <c>*</c> item holds at a step: the innermost macro with a signature it was
    /// expanded from, or the routine.
    /// </summary>
    private string Owner(Step step, Symbol routine)
    {
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && model.MacroAt(call) is { MacroSignature: not null } macro)
                return macro.DisplayName + "!";
        }
        return routine.DisplayName;
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
            if (item.Part is not (StatePart.A or StatePart.Index) || !IsKnown(item.Width))
            {
                ReportAt(item.Node, step, "`.ensure` makes widths hold, and takes `a8`, `a16`, `i8` and `i16`: "
                    + $"`{item.Text}` is not one of them");
                continue;
            }
            if (item.Width == Width.Sixteen && processor.E != ProcessorMode.Native)
            {
                ReportAt(item.Node, step, $"`.ensure {item.Text}` needs native mode, and "
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
    private FlowState Framed(Step step, FlowState state)
    {
        var statement = step.Statement;
        if (statement.ChildTokens.Length < 2 || model.SymbolAt(statement.ChildTokens[1]) is not { Kind: SymbolKind.Frame } frame)
            return state;
        if (statement.ChildNodes.FirstOrDefault() is not { } type
            || model.SymbolOf(type) is not { IsLayout: true, Size: { } size })
        {
            Report(step, $"`.frame {frame.DisplayName}` is laid out as a struct or a union, whose size says how many bytes it names");
            return state;
        }
        if (state.Stack is not { } stack)
            return state with { Stack = AnalysisStack.OnlyFrame(frame, (int)size) };
        if (stack.Framed(frame, (int)size) is { } framed)
            return state with { Stack = framed };
        Report(step, $"`{frame.DisplayName}` is {size} bytes, and only {stack.Depth} are pushed here");
        return state;
    }

    /// <summary>
    /// A frame's member named in an operand, which is only a place on the stack: it is a
    /// stack-relative operand, and its offset counts every byte pushed since the frame.
    /// </summary>
    private void Slot(Step step, AddressingMode? mode, AnalysisStack? stack)
    {
        if (step.Statement.ChildNodes.FirstOrDefault() is not { } operand)
            return;
        foreach (var name in operand.DescendantNodes().Where(node => node.Kind == SyntaxKind.NameExpression))
        {
            var tokens = name.ChildTokens;
            if (tokens.Length == 0 || model.SymbolAt(tokens[0]) is not { Kind: SymbolKind.Frame } frame)
                continue;
            var written = name.GetText().Trim();
            if (mode is not (AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
                || !name.Parent!.Kind.ToString().EndsWith("Operand", StringComparison.Ordinal))
            {
                ReportAt(name, step, $"`{written}` is a place on the stack, and is named only on its own as a "
                    + $"stack-relative operand: `{written},s`");
                continue;
            }
            if (stack is null)
            {
                ReportAt(name, step, $"`{written}` is counted from the stack pointer, and how much is pushed is not known here");
                continue;
            }
            if (stack.Above(frame) is not { } above)
            {
                ReportAt(name, step, $"`{written}` is in `{frame.DisplayName}`, which is no longer on the stack here");
                continue;
            }
            var size = frame.TypeExpression is { } type ? model.SymbolOf(type)?.Size ?? 0 : 0;
            var offset = tokens.Length > 1 ? model.ValueOf(name, step.On).AsNumber() ?? 0 : 0;

            // The frame's lowest byte is its last member's, and `1,s` is the byte on top.
            if (final)
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
        if (step.Statement.Kind == SyntaxKind.BlockSplice)
        {
            if (!step.Closes)
            {
                started[key] = processor;
            }
            else if (started.TryGetValue(key, out var before) && before != processor
                && step.On?.NearestCall is { } call && model.MacroAt(call) is { } owner)
            {
                Report(step, $"the block given to `{owner.DisplayName}!` has to leave the state as it found it: "
                    + $"it starts with `{before}` and ends with `{processor}`");
            }
            return state;
        }

        if (model.MacroAt(step.Statement) is not { MacroSignature: { } signature } macro)
            return state;
        var name = macro.DisplayName + "!";
        if (!step.Closes)
        {
            started[key] = processor;
            CheckEntry(step, $"`{name}`", signature, processor);
            return state with { Processor = signature.Entry };
        }
        CheckExit(step, "", "at the end of its body", signature.Exit, processor, name);
        return state with
        {
            Processor = Exited(signature, started.TryGetValue(key, out var at) ? at : ProcessorState.Unknown),
        };
    }

    /// <summary>
    /// Outside any routine there is no processor state: code that changes or depends on it
    /// belongs in a proc, where the analysis can follow it.
    /// </summary>
    private void CheckOutsideRoutines()
    {
        foreach (var step in layout.Steps)
        {
            if (step.Routine is not null || step.Label is not null)
                continue;
            var statement = step.Statement;
            if (statement.Kind is SyntaxKind.StateDirective or SyntaxKind.EnsureDirective or SyntaxKind.FrameDirective)
            {
                Report(step, $"`{statement.ChildTokens[0].Text.ToLowerInvariant()}` describes a point in a routine, "
                    + "and this is outside any `.proc`");
                continue;
            }

            if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
                continue;

            var mnemonic = statement.ChildTokens[0].Text.ToLowerInvariant();
            if (OperandOf(step) is { } operand && CodeLayout.ThroughDirectPage(operand))
            {
                Report(step, "`d:` is reached through the direct page, which only a `.proc` knows: outside one there "
                    + "is nothing to follow D through");
            }
            else if (mnemonic is "rep" or "sep" or "xce" or "plp")
            {
                Report(step, $"`{mnemonic}` changes the processor state, which only a `.proc` has: outside one "
                    + "there is nothing to follow it through");
            }
            else if (layout.Of(statement, step.On)?.Mode == AddressingMode.Immediate
                && Instructions.SizedBy(mnemonic) is { } register)
            {
                Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, which only a `.proc` has");
            }
        }
    }

    private void Report(Step step, string message) => ReportAt(step.Statement, step, message);

    /// <summary>
    /// Reports what is wrong with <paramref name="node"/> on the writing <paramref name="step"/>
    /// is. A line of a macro body is wrong only for the call that expanded it, so it is
    /// reported at that call, which is the side that can change, with the body line named
    /// beside it. A line a call gave as a block argument is the caller's own, and is reported
    /// where it stands.
    /// </summary>
    private void ReportAt(SyntaxNode node, Step step, string message)
    {
        if (!final)
            return;

        var inBody = node.Tree != model.Tree;
        SyntaxNode? call = null;
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is null)
                continue;
            call = level.Call;
            if (level.Body is { } body && body.Tree == node.Tree
                && node.Position >= body.Position && node.Position < body.Position + body.Green.FullWidth)
            {
                inBody = true;
            }
        }

        if (!inBody || call is null)
        {
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));
            return;
        }
        diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
            [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]));
    }
}
