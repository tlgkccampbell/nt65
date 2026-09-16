using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// The 65816's register widths and emulation flag, tracked through each routine. 65816 code
/// cannot be written without them, because they decide how wide an immediate is, and the
/// analysis stays small because the language keeps it inside one routine: every routine
/// declares its state at entry and exit, and control either stays in the routine or goes
/// to another routine's entry.
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
    private readonly List<Diagnostic> diagnostics = [];

    // Whether the walk is the last one, over converged states, which is the only one that
    // reports and records.
    private bool final;

    private StateAnalysis(SemanticModel model, CodeLayout layout, ControlFlow flow)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
    }

    /// <summary>What is wrong with the widths, the mode and the calls in this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// The most times any one block was walked before its region settled, counting the walk
    /// that found nothing had changed. This is what measures how quickly the analysis
    /// converges.
    /// </summary>
    public int MostWalks { get; private set; }

    /// <summary>Works out the processor state through every routine of <paramref name="flow"/>'s file.</summary>
    public static StateAnalysis Of(SemanticModel model, CodeLayout layout, ControlFlow flow)
    {
        var analysis = new StateAnalysis(model, layout, flow);
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
    /// The state reaching a statement, whichever writing of it is asked about. An editor asks
    /// about a line, and is shown the state that reaches its first writing.
    /// </summary>
    public FlowState? AnyBefore(SyntaxNode statement) =>
        reaching.Where(pair => pair.Key.Position == statement.Position)
            .Select(pair => pair.Value)
            .FirstOrDefault();

    /// <summary>What a routine's state is when it is entered: its declared entry, with nothing pushed.</summary>
    private static FlowState Entry(Signature signature) => new(signature.Entry, AnalysisStack.Empty);

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
        callee.Exit.E == ProcessorMode.Unchanged ? state.E : callee.Exit.E);

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
            reached[0] = Entry(signature);
            pending.Add(0);
        }
        Settle();

        // A label a `.state` declares is an entry point in its own right. One that some path
        // already reaches is checked against that path; one nothing reaches starts from what
        // the directive says, over a state otherwise unknown.
        foreach (var block in blocks)
        {
            if (block.IsDeclared && reached[block.Index] is null)
            {
                reached[block.Index] = new FlowState(ProcessorState.Unknown, null);
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
            if (final)
                reaching[(step.Statement.Position, step.On)] = state;
            var previous = i > 0 ? block.Steps[i - 1].Statement : null;
            var next = i == block.Steps.Count - 1 ? block.Next : null;
            state = Through(step, previous, next, state, routine);
        }
        return state;
    }

    /// <summary>What one statement does to the state.</summary>
    private FlowState Through(Step step, SyntaxNode? previous, SyntaxNode? next, FlowState state, Symbol routine)
    {
        var statement = step.Statement;
        if (statement.Kind == SyntaxKind.StateDirective)
            return Asserted(step, state);
        if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
            return state;

        var mnemonic = statement.ChildTokens[0].Text.ToLowerInvariant();
        var mode = layout.Of(statement, step.On)?.Mode;
        var processor = state.Processor;
        var stack = state.Stack;
        if (mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) is { } register)
            CheckImmediate(step, mnemonic, register, processor, routine);

        switch (mnemonic)
        {
            case "rep":
            case "sep":
                return state with { Processor = Flags(step, mnemonic == "rep", processor) };

            // `clc` then `xce` enters native mode, and `sec` then `xce` emulation mode. Any
            // other `xce` swaps in a carry nobody knows.
            case "xce":
                if (previous is not null && Is(previous, "clc"))
                {
                    return state with
                    {
                        Processor = processor.E switch
                        {
                            ProcessorMode.Emulation => new ProcessorState(Width.Eight, Width.Eight, ProcessorMode.Native),
                            ProcessorMode.Native => processor,
                            _ => new ProcessorState(Width.Unknown, Width.Unknown, ProcessorMode.Native),
                        },
                    };
                }
                return state with
                {
                    Processor = previous is not null && Is(previous, "sec")
                        ? new ProcessorState(Width.Eight, Width.Eight, ProcessorMode.Emulation)
                        : ProcessorState.Unknown,
                };

            case "php":
                return state with { Stack = stack?.Push(new StackEntry(true, processor.A, processor.Index)) };
            case "pha":
                return state with { Stack = Push(stack, Bytes(processor.A)) };
            case "phx":
            case "phy":
                return state with { Stack = Push(stack, Bytes(processor.Index)) };
            case "phb":
            case "phk":
                return state with { Stack = Push(stack, 1) };
            case "phd":
            case "pea":
            case "pei":
            case "per":
                return state with { Stack = Push(stack, 2) };
            case "pla":
                return state with { Stack = Pull(stack, Bytes(processor.A)) };
            case "plx":
            case "ply":
                return state with { Stack = Pull(stack, Bytes(processor.Index)) };
            case "plb":
                return state with { Stack = Pull(stack, 1) };
            case "pld":
                return state with { Stack = Pull(stack, 2) };

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
        if (calls)
        {
            var named = next is null ? [] : Routines(next, step.On).ToList();
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
    /// the parts it gives are checked.
    /// </summary>
    private ProcessorState? DeclaredElsewhere(Symbol label, Symbol routine)
    {
        foreach (var region in flow.Regions)
        {
            if (region.Routine == routine)
                continue;
            if (region.Blocks.FirstOrDefault(block => block.Label == label && block.IsDeclared) is not { } declared)
                continue;
            var state = ProcessorState.Unknown;
            foreach (var item in StateItem.Read(declared.Steps[0].Statement))
            {
                state = item.Part switch
                {
                    StatePart.A => state with { A = item.Width },
                    StatePart.Index => state with { Index = item.Width },
                    StatePart.E => state with { E = item.Mode },
                    _ => state,
                };
            }
            return state;
        }
        return null;
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
            Report(step, $"`{target.DisplayName}` is near, and is jumped to with `jmp`");
        else if (mnemonic is not ("jml" or ".next") && callee.IsFar)
            Report(step, $"`{target.DisplayName}` is far, and is jumped to with `jml`");
        CheckEntry(step, what, callee, state);

        if (callee.IsFar != own.IsFar)
        {
            Report(step, $"{what} is a tail call, and `{target.DisplayName}` is {callee.Distance} while "
                + $"`{routine.DisplayName}` is {own.Distance}: it would return to the caller the wrong way");
        }
        CheckExit(step, $"{what} is a tail call:", $"when `{target.DisplayName}` returns",
            own.Exit, Exited(callee, state), routine);
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

        void Width(string item, string register, Width needed, Width here)
        {
            if (!IsKnown(needed) || needed == here)
                return;
            Report(step, $"{what} needs `{ProcessorState.Spell(item, needed)}`, and "
                + (IsKnown(here) ? $"{register} {(register == "A" ? "is" : "are")} {Spell(here)} here"
                    : $"the width of {register} is not known here"));
        }
    }

    /// <summary>That <paramref name="state"/> is what <paramref name="routine"/> declares it returns with.</summary>
    /// <remarks><paramref name="where"/> says where <paramref name="state"/> holds, as the message puts it.</remarks>
    private void CheckExit(
        Step step, string what, string where, ProcessorState exit, ProcessorState state, Symbol routine)
    {
        Part("a", "A", exit.A, state.A);
        Part("i", "X and Y", exit.Index, state.Index);
        if (exit.E == ProcessorMode.Unchanged && state.E != ProcessorMode.Unchanged)
        {
            Report(step, $"{what} `{routine.DisplayName}` says `e*`, so the mode must be what it was on entry, "
                + $"and {where} it may not be");
        }
        else if (IsKnown(exit.E) && exit.E != state.E)
        {
            Report(step, $"{what} `{routine.DisplayName}` returns in {Mode(exit.E)} mode, and "
                + (IsKnown(state.E)
                    ? $"the processor is in {Mode(state.E)} mode {where}"
                    : $"the mode is not known {where}"));
        }

        void Part(string item, string register, Width declared, Width here)
        {
            if (declared == Width.Unchanged && here != Width.Unchanged)
            {
                Report(step, $"{what} `{routine.DisplayName}` says `{item}*`, so {register} must be as wide as it "
                    + $"was on entry, and {where} it may not be");
            }
            else if (IsKnown(declared) && declared != here)
            {
                Report(step, $"{what} `{routine.DisplayName}` returns with `{ProcessorState.Spell(item, declared)}`, and "
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
        CheckExit(step, $"`{mnemonic}`:", "here", signature.Exit, state, routine);
    }

    /// <summary>
    /// A width-dependent immediate, which ca65 sizes from the width it is told. The analysis
    /// is what tells it, so the width has to be known here.
    /// </summary>
    private void CheckImmediate(Step step, string mnemonic, WidthRegister register, ProcessorState state, Symbol routine)
    {
        var width = state.Of(register);
        var item = register == WidthRegister.A ? "a" : "i";
        if (width == Width.Unchanged)
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and `{routine.DisplayName}` says "
                + $"`{item}*`, which assumes nothing about it");
        }
        else if (!IsKnown(width))
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and it is not known here: "
                + "a `.state` says what it is");

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
        var written = step.Statement.ChildNodes.FirstOrDefault();
        var operand = Operands.Substituted(model, written, step.On)?.Operand ?? written;
        var expression = operand?.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix);
        if (expression is null || model.ValueOf(expression, step.On).AsNumber() is not { } flags)
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

                // The direct page and the data bank are checked by the analysis that tracks them.
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
            if (statement.Kind == SyntaxKind.StateDirective)
            {
                Report(step, "`.state` describes a point in a routine, and this is outside any `.proc`");
                continue;
            }
            if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
                continue;

            var mnemonic = statement.ChildTokens[0].Text.ToLowerInvariant();
            if (mnemonic is "rep" or "sep" or "xce" or "plp")
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
