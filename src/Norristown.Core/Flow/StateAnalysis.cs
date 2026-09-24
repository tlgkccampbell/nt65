using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Tracks the 65816's register widths, emulation flag, direct page and data bank through each
/// routine. 65816 code cannot be written without them. The widths decide how wide an immediate
/// is, and D and B decide what memory an operand reaches. The analysis stays small because the
/// language keeps it inside one routine. Every routine declares its state at entry and exit, and
/// control either stays in the routine or goes to another routine's entry.
/// <para>
/// Each region's blocks are run to a fixed point over a lattice of known values and unknown,
/// starting from the routine's signature and from every label a <c>.state</c> declares. What is
/// reported comes from one pass over the converged states. A merge never reports, and an unknown
/// value is an error only where it is used. This type works out what each statement does to the
/// state, and <see cref="StateChecks"/> reports what is wrong with it.
/// </para>
/// </summary>
public sealed class StateAnalysis : IProcessorStates
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly StateChecks checks;
    private readonly OutsideEntries outside;
    private readonly Dictionary<(int Position, Expansion? On), FlowState> reaching = [];
    private readonly Dictionary<(int Position, Expansion? On), int> slots = [];

    // The state at the start of each expansion of a macro with a signature, and of each block
    // spliced into one. A spliced block's end is checked against it, and a macro's exit state
    // takes its unchanged parts from it.
    private readonly Dictionary<(int Position, Expansion? On), ProcessorState> started = [];

    private StateAnalysis(SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange> ranges)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        checks = new StateChecks(model, layout, ranges);
        outside = new OutsideEntries(model, layout);
    }

    /// <summary>Gets what is wrong with the widths, the mode and the calls in this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Gets the most times any one block was walked before its region reached a fixed point,
    /// counting the walk that found nothing had changed. This measures how quickly the analysis
    /// converges.
    /// </summary>
    public int MaximumWalks { get; private set; }

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
        analysis.checks.CheckOutsideRoutines();
        analysis.Diagnostics = Norristown.Diagnostics.Ordered(
            analysis.checks.Found.DistinctBy(d => (d.Span, d.Id, d.Message)));
        return analysis;
    }

    /// <summary>
    /// Returns the state reaching <paramref name="statement"/> in the expansion
    /// <paramref name="on"/>, or null where nothing reaches it or it is in no routine.
    /// </summary>
    public FlowState? Before(SyntaxNode statement, Expansion? on = null) =>
        reaching.GetValueOrDefault((statement.Position, on));

    /// <summary>
    /// Returns the <c>n</c> of <c>n,s</c> that the frame slot in <paramref name="statement"/>'s
    /// operand comes to in the expansion <paramref name="on"/>. It returns null where the operand
    /// names no slot or the slot is not known.
    /// </summary>
    public int? SlotAt(SyntaxNode statement, Expansion? on = null) =>
        slots.TryGetValue((statement.Position, on), out var slot) ? slot : null;

    /// <summary>
    /// Returns the processor's own part of the state <see cref="Before"/> returns, which is all
    /// layout asks of the analysis.
    /// </summary>
    /// <param name="statement">The statement.</param>
    /// <param name="on">The expansion of it being asked about.</param>
    /// <returns>The processor state reaching it, or null.</returns>
    ProcessorState? IProcessorStates.Before(SyntaxNode statement, Expansion? on) =>
        Before(statement, on)?.Processor;

    /// <summary>
    /// Returns the state reaching a statement in any <see cref="Expansion"/> of it. An editor asks
    /// about a line, and is shown the state that reaches the first expansion found. An expansion's
    /// line belongs to the file that contains the body it expands. For a macro declared in another
    /// file that is the other file, so the same position in two files is two lines.
    /// </summary>
    public FlowState? AnyBefore(SyntaxNode statement) =>
        reaching.Where(pair => pair.Key.Position == statement.Position
                && (pair.Key.On?.Body?.Tree ?? model.Tree) == statement.Tree)
            .Select(pair => pair.Value)
            .FirstOrDefault();

    /// <summary>
    /// Returns a routine's state when it is entered. That is its declared entry, with nothing pushed
    /// except, for a routine that takes <c>args n</c>, the arguments and the return address above
    /// them.
    /// </summary>
    private static FlowState Entry(Signature signature, Symbol routine) => new(signature.Entry, EntryStack(signature))
    {
        WhyA = signature.Entry.A == Width.Unknown ? EntryCause(signature, routine, "a?") : null,
        WhyIndex = signature.Entry.Index == Width.Unknown ? EntryCause(signature, routine, "i?") : null,
    };

    /// <summary>
    /// Returns the analysis stack where a routine is entered. It is empty except, for a routine
    /// that takes <c>args n</c>, the arguments and the return address above them.
    /// </summary>
    private static AnalysisStack EntryStack(Signature signature) => signature.Arguments > 0
        ? AnalysisStack.Empty.Push(StackEntry.Opaque, signature.Arguments + (signature.IsFar ? 3 : 2))
        : AnalysisStack.Empty;

    private static Cause EntryCause(Signature signature, Symbol routine, string item) => signature.IsInterrupt
        ? new($"`{routine.DisplayName}` is an interrupt handler, entered from anywhere", "an `.ensure` sets it")
        : new($"`{routine.DisplayName}` declares `{item}` at entry", "an `.ensure` sets it");

    /// <summary>
    /// Returns how many bytes a push or pull of a register this wide moves, or null when that is
    /// not known.
    /// </summary>
    private static int? Bytes(Width width) => width switch
    {
        Width.Eight => 1,
        Width.Sixteen => 2,
        _ => null,
    };

    /// <summary>
    /// Returns the value a <c>dp = e</c>, <c>dbr = e</c> or <c>dbr = [...]</c> item gives, or
    /// unknown for <c>dp?</c> and for a value nt65 cannot work out.
    /// </summary>
    private static StateValue ValueOf(StateItem item, SemanticModel model)
    {
        if (item.IsBankSet)
        {
            return item.Part == StatePart.DataBank
                && item.BanksOf(expression => model.ValueOf(expression).AsNumber(), out _) is { } banks
                    ? StateValue.Among(banks)
                    : StateValue.Unknown;
        }
        return item.Expression is { } given && model.ValueOf(given).AsNumber() is { } value
            ? StateValue.Of(value)
            : StateValue.Unknown;
    }

    /// <summary>
    /// Returns what is known where control arrives from outside the routine's own paths. Nothing
    /// is known, except that a part the routine's signature leaves unchanged is assumed to be
    /// unchanged on the way there too. So a label needs to declare a part only where the routine's
    /// signature gives that part a specific value.
    /// </summary>
    private static ProcessorState Outside(Signature signature)
    {
        var entry = signature.Entry;
        return new ProcessorState(
            entry.A == Width.Unchanged ? Width.Unchanged : Width.Unknown,
            entry.Index == Width.Unchanged ? Width.Unchanged : Width.Unknown,
            entry.E == ProcessorMode.Unchanged ? ProcessorMode.Unchanged : ProcessorMode.Unknown,
            entry.D.IsEntered ? entry.D : StateValue.Unknown,
            entry.B.IsEntered ? entry.B : StateValue.Unknown);
    }

    /// <summary>
    /// Returns which parts of the state a declared label's <c>.state</c> gives. These are the parts a
    /// jump into the label is checked against, and so the only parts the code after the label
    /// may rely on. <c>?</c> gives them all, as unknown.
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

    /// <summary>
    /// Returns the cause for a width at a declared label being unknown, which is that the
    /// declaration does not say.
    /// </summary>
    private static Cause Undeclared(Symbol label, Symbol routine, StateRegister register) => new(
        $"`{label.DisplayName}` can be entered from outside `{routine.DisplayName}`, and its `.state` does not "
            + $"declare the width of {register.Name}",
        $"the `.state` after `{label.DisplayName}` can declare `{register.Item}8` or `{register.Item}16`");

    private static AnalysisStack? Push(AnalysisStack? stack, int? bytes) =>
        bytes is { } count ? stack?.Push(StackEntry.Opaque, count) : null;

    private static AnalysisStack? Pull(AnalysisStack? stack, int? bytes) =>
        bytes is { } count ? stack?.Pull(count) : null;

    /// <summary>
    /// Runs one region to a fixed point, then once more to report.
    /// </summary>
    private void Analyze(FlowRegion region)
    {
        var blocks = region.Blocks;
        var signature = region.Routine.Signature ?? Signature.Default;
        var walks = new int[blocks.Count];
        var solver = new Dataflow<FlowState>(
            blocks,
            (block, state) =>
            {
                walks[block.Index]++;
                return Walk(block, state, region, null);
            },
            FlowState.Merge,
            block => flow.Onward(blocks, block));

        if (region.IsEntered && blocks.Count > 0)
            solver.Enter(0, Entry(signature, region.Routine));

        // A label a `.state` declares is an entry point in its own right. If no path reaches
        // it, it starts from what the directive says, over an otherwise unknown state and the
        // stack a call to the routine leaves, since a jump in arrives as a call would. If some
        // path already reaches it, the directive is checked against that path. Where the label
        // can also be entered from outside the routine, only the parts the declaration gives
        // are kept, because what the paths inside leave is no promise to code that jumps in.
        solver.EnterDeclared(
            outside,
            new FlowState(Outside(signature), EntryStack(signature)),
            (block, state) => Entered(block, state, signature, region.Routine));

        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is { } state)
                Walk(block, state, region, checks);
            else
                checks.Unreached(block, region);
        }
        MaximumWalks = Math.Max(MaximumWalks, walks.DefaultIfEmpty().Max());
    }

    /// <summary>
    /// Returns the state at a declared label that can be entered from outside the routine. The
    /// parts its <c>.state</c> gives keep what reaches the label, which the directive itself then
    /// checks. Every part it leaves out becomes unknown, because a jump from outside is checked
    /// for the parts the declaration gives and for nothing else. The stack is what a call to the
    /// routine leaves, since that is what a jump in arrives with. It is unknown where the path
    /// above the label has pushed something else, because a declaration cannot say what is on
    /// the stack, so nothing can reconcile the two.
    /// </summary>
    private static FlowState Entered(
        BasicBlock block, FlowState reached, Signature signature, Symbol routine)
    {
        var given = Given(block);
        var here = reached.Processor;
        var outside = Outside(signature);
        var label = block.Label!;
        var a = given.Contains(StatePart.A) ? here.A : Met(here.A, outside.A);
        var index = given.Contains(StatePart.Index) ? here.Index : Met(here.Index, outside.Index);
        var stack = AnalysisStack.Merge(reached.Stack, EntryStack(signature));
        return reached with
        {
            Processor = new ProcessorState(
                a,
                index,
                given.Contains(StatePart.E) ? here.E : here.E == outside.E ? here.E : ProcessorMode.Unknown,
                given.Contains(StatePart.DirectPage) ? here.D : StateValue.Merge(here.D, outside.D),
                given.Contains(StatePart.DataBank) ? here.B : StateValue.Merge(here.B, outside.B)),
            Stack = stack,
            WhyA = a == Width.Unknown && !given.Contains(StatePart.A)
                ? Undeclared(label, routine, StateRegister.A)
                : reached.WhyA,
            WhyIndex = index == Width.Unknown && !given.Contains(StatePart.Index)
                ? Undeclared(label, routine, StateRegister.Index)
                : reached.WhyIndex,
            WhyStack = stack is null ? OutsideEntries.UnknownStack(label, routine) : reached.WhyStack,
        };

        static Width Met(Width here, Width outside) => here == outside ? here : Width.Unknown;
    }

    /// <summary>
    /// Returns the state after one block, from the state that reaches it. The final walk, over
    /// converged states, reports to <paramref name="report"/> and records the state reaching each
    /// statement. A walk toward the fixed point passes null, and neither reports nor records.
    /// </summary>
    private FlowState Walk(BasicBlock block, FlowState state, FlowRegion region, StateChecks? report)
    {
        var routine = region.Routine;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            if (report is not null && !step.Closes)
                reaching[(step.Statement.Position, step.On)] = state;
            Step? previous = i > 0 ? block.Steps[i - 1] : null;
            var next = i == block.Steps.Count - 1 ? block.Next : null;
            state = Explained(step, next, state, Through(step, previous, next, state, routine, report));
        }
        return state;
    }

    /// <summary>
    /// Returns the state after <paramref name="step"/>, with a cause for each width it made
    /// unknown, and the earlier cause kept for each width it left unknown.
    /// </summary>
    private FlowState Explained(Step step, NextDirectiveSyntax? next, FlowState before, FlowState after) => after with
    {
        WhyA = Why(step, next, before, before.Processor.A, after.Processor.A, before.WhyA),
        WhyIndex = Why(step, next, before, before.Processor.Index, after.Processor.Index, before.WhyIndex),
    };

    private Cause? Why(
        Step step, NextDirectiveSyntax? next, FlowState state, Width before, Width after, Cause? inherited)
    {
        if (after != Width.Unknown)
            return null;
        if (before == Width.Unknown)
            return inherited;

        if (step.Statement is StateDirectiveSyntax)
            return new("a `.state` declares it unknown", "the `.state` can declare what it is");
        if (step.Statement is not InstructionStatementSyntax statement)
            return null;
        var mode = state.Processor.E;
        var quoted = $"`{statement.GetText().Trim()}`";
        return statement.MnemonicKind switch
        {
            // A `plp` that finds no saved P because the stack itself is not known reports what
            // lost the stack, which is nearer the mistake than the `php` above it.
            MnemonicKind.Plp when state.Stack is null && state.WhyStack is { } lost => lost,
            MnemonicKind.Plp => new($"{quoted} pulls a status that no `php` in this routine pushed", "an `.ensure` after it sets it"),
            MnemonicKind.Xce => new($"{quoted} follows neither `clc` nor `sec`", "a `.state` after it declares what it is"),
            MnemonicKind.Rep when mode != ProcessorMode.Native && Constant(step) is not null
                => new($"{quoted} widens nothing in emulation mode, and the mode is not known", "a `.state` before it declares which mode it is"),
            MnemonicKind.Rep or MnemonicKind.Sep => new($"{quoted} changes flags nt65 cannot work out", "an `.ensure` after it sets it"),
            MnemonicKind.Jsr or MnemonicKind.Jsl when next is not null && statement.Operand is not AbsoluteOperandSyntax
                => new($"{quoted} calls through a pointer, and its `.next` names no routine", "a `.next` that names them lets their exit state flow here"),
            MnemonicKind.Jsr or MnemonicKind.Jsl => new($"{quoted} returns with it unknown", "an `.ensure` after it sets it"),
            _ => new($"{quoted} makes it unknown", "a `.state` after it declares what it is"),
        };
    }

    /// <summary>Returns what one statement does to the state.</summary>
    private FlowState Through(
        Step step, Step? previous, NextDirectiveSyntax? next, FlowState state, Symbol routine, StateChecks? report)
    {
        if (step.Statement is StateDirectiveSyntax)
            return Asserted(step, state, report);
        if (step.Statement is EnsureDirectiveSyntax)
            return Ensured(step, state, report);
        if (step.Statement is FrameDirectiveSyntax frame)
            return Framed(step, frame, state, report);
        if (step.IsMarker)
            return Marked(step, state, report);

        // Running off the end into the routine a `.fallthrough` names is a tail call to it.
        if (step.Statement is FallthroughDirectiveSyntax { Target: { } into }
            && Targets.Of(model, into, step.On)?.Symbol is { Kind: SymbolKind.Proc, Signature: { } signature } runsInto)
        {
            report?.CheckTailCall(step, ".fallthrough", MnemonicKind.None, runsInto, signature, state.Processor, routine);
            return state;
        }

        // Flow that runs into data goes where the data's `.next` says, which is treated as a
        // jump to each place named.
        if (step.Statement is not InstructionStatementSyntax statement)
        {
            if (report is not null && next is not null && step.Statement is DataDirectiveSyntax or DataValuesSyntax)
                CheckNamed(step, next, state, routine, report);
            return state;
        }

        var mnemonic = statement.MnemonicKind;
        var mode = layout.Of(statement, step.On)?.Mode;
        var processor = state.Processor;
        var stack = state.Stack;
        if (mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) is { } register)
        {
            report?.CheckImmediate(
                step, mnemonic, register, processor, register == WidthRegister.A ? state.WhyA : state.WhyIndex, routine);
        }
        if (report is not null)
            Slot(step, mode, state, report);
        report?.CheckMemory(step, mnemonic, mode, processor, routine);

        switch (mnemonic)
        {
            case MnemonicKind.Rep:
            case MnemonicKind.Sep:
                return state with { Processor = Flags(step, mnemonic == MnemonicKind.Rep, processor) };

            // `clc` then `xce` enters native mode, and `sec` then `xce` emulation mode. Any
            // other `xce` swaps in an unknown carry, so the mode becomes unknown.
            // D and B are unaffected.
            case MnemonicKind.Xce:
                if (previous is { Statement: InstructionStatementSyntax { MnemonicKind: MnemonicKind.Clc } })
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
                    Processor = previous is { Statement: InstructionStatementSyntax { MnemonicKind: MnemonicKind.Sec } }
                        ? processor with { A = Width.Eight, Index = Width.Eight, E = ProcessorMode.Emulation }
                        : processor with { A = Width.Unknown, Index = Width.Unknown, E = ProcessorMode.Unknown },
                };

            // The direct page is loaded from a constant by `lda #c` then `tcd` with A 16 bits.
            // Any other `tcd` leaves D unknown.
            case MnemonicKind.Tcd:
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
            case MnemonicKind.Mvn:
            case MnemonicKind.Mvp:
                return state with { Processor = processor with { B = MovedTo(step) } };

            case MnemonicKind.Php:
                return state with { Stack = stack?.Push(new StackEntry(true, processor.A, processor.Index)) };
            // A constant loaded into A just before it is pushed is a value a pull can get back,
            // so `lda #c`, `pha`, `plb` loads the data bank.
            case MnemonicKind.Pha:
                return state with
                {
                    Stack = Bytes(processor.A) is { } bytes && previous is { } loader && Loaded(loader) is { } loaded
                        ? stack?.PushValue(StateValue.Of(loaded & (bytes == 1 ? 0xff : 0xffff)), bytes)
                        : Push(stack, Bytes(processor.A)),
                };
            case MnemonicKind.Phx:
            case MnemonicKind.Phy:
                return state with { Stack = Push(stack, Bytes(processor.Index)) };
            case MnemonicKind.Phb:
                return state with { Stack = stack?.PushValue(processor.B, 1) };
            case MnemonicKind.Phk:
                return state with { Stack = stack?.PushValue(checks.BankOf(step.Segment), 1) };
            case MnemonicKind.Phd:
                return state with { Stack = stack?.PushValue(processor.D, 2) };
            case MnemonicKind.Pea:
                return state with
                {
                    Stack = stack?.PushValue(Constant(step) is { } pushed ? StateValue.Of(pushed & 0xffff) : StateValue.Unknown, 2),
                };
            case MnemonicKind.Pei:
            case MnemonicKind.Per:
                return state with { Stack = Push(stack, 2) };
            case MnemonicKind.Pla:
                return state with { Stack = Pull(stack, Bytes(processor.A)) };
            case MnemonicKind.Plx:
            case MnemonicKind.Ply:
                return state with { Stack = Pull(stack, Bytes(processor.Index)) };
            // A pull that finds a value the routine pushed gets it back. That value may be a saved D
            // or B, a constant, or the program bank. Any other pull leaves the register unknown.
            case MnemonicKind.Plb:
                return new FlowState(processor with { B = stack?.PulledValue(1) ?? StateValue.Unknown }, Pull(stack, 1));
            case MnemonicKind.Pld:
                return new FlowState(processor with { D = stack?.PulledValue(2) ?? StateValue.Unknown }, Pull(stack, 2));

            // A pull that finds the status register a `php` saved restores the widths saved
            // with it. Any other pull leaves them unknown. The emulation flag is not in it.
            case MnemonicKind.Plp:
                var restored = stack?.Top is { IsStatus: true } saved
                    ? processor.E == ProcessorMode.Emulation
                        ? processor with { A = Width.Eight, Index = Width.Eight }
                        : processor with { A = saved.A, Index = saved.Index }
                    : processor with { A = Width.Unknown, Index = Width.Unknown };
                return new FlowState(restored, Pull(stack, 1));

            // The stack pointer now points somewhere unknown, and what is pushed from here on
            // is tracked on top of that unknown base.
            case MnemonicKind.Txs:
            case MnemonicKind.Tcs:
                return state with { Stack = AnalysisStack.Unanchored };

            // A return with a `.next` is a jump to the address the routine pushed, and pulls
            // it. Where the `.next` names routines it is a tail call to each of them, checked
            // as a `jmp` to one would be.
            case MnemonicKind.Rts:
            case MnemonicKind.Rtl:
                if (next is not null)
                {
                    foreach (var named in Routines(next, step.On))
                        report?.CheckTailCall(step, ".next", MnemonicKind.None, named, named.Signature!, processor, routine);
                    return state with { Stack = Pull(stack, mnemonic == MnemonicKind.Rts ? 2 : 3) };
                }
                if (routine.Signature is not { HasNoCaller: true })
                    report?.CheckReturn(step, mnemonic, processor, routine);
                return state;

            default:
                return Transferred(step, mnemonic, mode, next, state, routine, report);
        }
    }

    /// <summary>
    /// Returns what a call or a jump does to the state. After a call the state becomes the called
    /// routine's exit, and a jump to a routine is checked as a tail call.
    /// </summary>
    private FlowState Transferred(
        Step step,
        MnemonicKind mnemonic,
        AddressingMode? mode,
        NextDirectiveSyntax? next,
        FlowState state,
        Symbol routine,
        StateChecks? report)
    {
        var statement = step.Statement;
        var transfer = Transfers.Of(statement, mode);
        var calls = Instructions.IsCall(mnemonic);
        var target = Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol;

        if (transfer == Transfer.Call)
        {
            report?.CheckMirror(step, mode);
            report?.CheckArguments(step, target, state.Stack, 0);
            return state with { Processor = Called(step, mnemonic, target, state.Processor, report) };
        }

        // A relative call comes back to the label after it, having pulled what the `per` and
        // any `phk` pushed.
        if (flow.RelativeCallAt(step) is { } relative)
        {
            report?.CheckArguments(step, relative.Routine, state.Stack, relative.Pushed);
            return new FlowState(
                RelativelyCalled(step, mnemonic, relative, state.Processor, report), Pull(state.Stack, relative.Pushed));
        }

        // An indirect call goes where its `.next` says, and it returns with what any of the
        // routines it names return with. With nothing named, nothing is known after it. With no
        // `.next` at all, that has been reported, and the state is left alone so the one
        // mistake is not reported again wherever the state is used.
        if (calls)
        {
            if (next is null)
                return state;
            var named = Routines(next, step.On).ToList();
            if (named.Count == 0)
                return state with { Processor = ProcessorState.Unknown };
            FlowState? merged = null;
            foreach (var each in named)
                merged = FlowState.Merge(merged, state with { Processor = Called(step, mnemonic, each, state.Processor, report) });
            return merged!;
        }

        if (transfer is Transfer.Jump or Transfer.Branch && target is { Signature: { } callee }
            && !checks.InAnotherSpace(step, target))
        {
            report?.CheckMirror(step, mode);
            report?.CheckTailCall(step, SyntaxFacts.TextOf(mnemonic), mnemonic, target, callee, state.Processor, routine);
        }
        else if (transfer is Transfer.Jump or Transfer.Branch && target is not null && Interior(target, routine) is { } owner)
        {
            if (DeclaredElsewhere(target) is { } declared)
                report?.CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", new Signature(declared, declared, false), state.Processor);
            report?.CheckJumpInto(step, SyntaxFacts.TextOf(mnemonic), target, owner, state.Processor, routine);
        }
        if (report is not null && next is not null)
            CheckNamed(step, next, state, routine, report);
        return state;
    }

    /// <summary>
    /// Checks each place a <c>.next</c> names as a jump from here. A jump to a routine's start is
    /// checked as a tail call, and a jump to a label inside another routine as a jump into it.
    /// </summary>
    private void CheckNamed(Step step, NextDirectiveSyntax next, FlowState state, Symbol routine, StateChecks report)
    {
        foreach (var named in flow.Named(next, step.On).Select(named => named.Symbol))
        {
            if (named.Signature is { } signature)
                report.CheckTailCall(step, ".next", MnemonicKind.None, named, signature, state.Processor, routine);
            else if (Interior(named, routine) is { } inside)
                report.CheckJumpInto(step, ".next", named, inside, state.Processor, routine);
        }
    }

    /// <summary>
    /// Returns the routines a <c>.next</c> names, leaving out the labels, which are handled as
    /// edges.
    /// </summary>
    private IEnumerable<Symbol> Routines(NextDirectiveSyntax next, Expansion? on) =>
        flow.Named(next, on).Select(named => named.Symbol).Where(symbol => symbol.Signature is not null);

    /// <summary>
    /// Returns the routine a target's label is inside, where the target is a label in another
    /// routine and so a jump into that routine's interior. It returns null for every other target.
    /// Another instance of the same <see cref="Family"/> does not count as another routine,
    /// because all the instances share one body in the source.
    /// </summary>
    private static Symbol? Interior(Symbol target, Symbol routine) =>
        target is { Kind: SymbolKind.Label, Routine: { } owner } && owner != routine && !owner.IsSiblingOf(routine)
            ? owner
            : null;

    /// <summary>
    /// Checks a call and returns the state after it. The state here must be what the routine
    /// expects, and becomes what it returns with, except for the parts it declares unchanged,
    /// which keep what they were.
    /// </summary>
    private ProcessorState Called(
        Step step, MnemonicKind mnemonic, Symbol? target, ProcessorState state, StateChecks? report)
    {
        // A call into another address space has been reported where it is laid out, and what
        // another processor's routine expects has no bearing on this processor's state.
        if (checks.InAnotherSpace(step, target))
            return state;
        if (target?.Signature is not { } callee)
        {
            report?.CheckCallTarget(step, mnemonic, target);

            // Nothing says what it returns with, and once the call is fixed its signature will.
            // Leaving the state alone keeps one mistake to one diagnostic.
            return state;
        }

        if (callee.IsInterrupt)
            return state;
        report?.CheckCall(step, mnemonic, target, callee, state);
        return StateChecks.Exited(callee, state);
    }

    /// <summary>
    /// Checks a call made with <c>per</c> and a branch, and returns the state after it. The call is
    /// checked as <c>jsr</c> or, with a <c>phk</c> before it, as <c>jsl</c>.
    /// </summary>
    private static ProcessorState RelativelyCalled(
        Step step, MnemonicKind mnemonic, RelativeCall call, ProcessorState state, StateChecks? report)
    {
        var callee = call.Routine.Signature!;
        if (callee.IsInterrupt)
            return state;
        report?.CheckRelativeCall(step, mnemonic, call, state);
        return StateChecks.Exited(callee, state);
    }

    /// <summary>
    /// Returns what a <c>.state</c> declares at a label inside another routine, which a jump into
    /// that routine has to meet, or null when the label declares nothing. Only the parts it gives are
    /// checked. The declaration is read off the label, so the routine may be in another file.
    /// </summary>
    private ProcessorState? DeclaredElsewhere(Symbol label)
    {
        if (label.StateDeclaration is not { } declared)
            return null;
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
    /// Returns the state after <c>rep #c</c> or <c>sep #c</c>. In native mode the widths it names
    /// become known. In emulation mode the widths are pinned at 8 and nothing changes. Where the
    /// mode is not known, a <c>sep</c> still makes them 8, which they are in either mode.
    /// </summary>
    private ProcessorState Flags(Step step, bool reset, ProcessorState state)
    {
        // Emulation mode pins both widths at 8 regardless of the operand, so an operand nt65
        // cannot work out changes nothing there. The mode is checked first, because forgetting
        // the widths and then finding the mode would throw away what the mode already said.
        if (state.E == ProcessorMode.Emulation)
            return state;
        if (Constant(step) is not { } flags)
            return state with { A = Width.Unknown, Index = Width.Unknown };

        var width = reset
            ? state.E == ProcessorMode.Native ? Width.Sixteen : Width.Unknown
            : Width.Eight;
        return state with
        {
            A = (flags & (long)StatusFlags.M) != 0 ? width : state.A,
            Index = (flags & (long)StatusFlags.X) != 0 ? width : state.Index,
        };
    }

    /// <summary>
    /// Returns the value of an instruction's operand, such as the <c>#c</c> of <c>rep #c</c> or the
    /// <c>c</c> of <c>pea c</c>, or null when it is not a constant.
    /// </summary>
    private long? Constant(Step step) =>
        checks.OperandOf(step) is { } operand && CodeLayout.Expression(operand) is { } expression
            ? model.ValueOf(expression, step.On).AsNumber()
            : null;

    /// <summary>
    /// Returns the constant <paramref name="step"/> loads into A, for an <c>lda #c</c>, or null for
    /// anything else.
    /// </summary>
    private long? Loaded(Step step) =>
        step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Lda }
        && layout.Of(step.Statement, step.On)?.Mode == AddressingMode.Immediate
            ? Constant(step)
            : null;

    /// <summary>
    /// Returns the destination bank of <c>mvn #src, #dst</c>, which is where it leaves the data
    /// bank. The destination is a constant, or <c>^sym</c>, the bank of a symbol whose segment
    /// declares one.
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
    /// Returns the state after a <c>.state</c>, reporting where it disagrees with the state here.
    /// Each item both asserts and sets a part of the state. Where the part is known and differs,
    /// that is an error. Where it is unknown, the item sets it, and an item with <c>?</c> makes it
    /// unknown.
    /// </summary>
    private FlowState Asserted(Step step, FlowState state, StateChecks? report)
    {
        var processor = state.Processor;
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.IsUnchanged || item.Part is StatePart.Distance or StatePart.Inline or StatePart.Arguments
                or StatePart.Interrupt or StatePart.NoReturn or StatePart.Set)
            {
                report?.ReportAt(item.Node, step, Catalogue.StateItemNotAPoint.Message(item.Text));
                continue;
            }
            switch (item.Part)
            {
                case StatePart.A:
                    processor = processor with { A = Set(StateRegister.A, processor.A, item) };
                    break;
                case StatePart.Index:
                    processor = processor with { Index = Set(StateRegister.Index, processor.Index, item) };
                    break;
                case StatePart.E:
                    if (StateChecks.IsKnown(item.Mode) && StateChecks.IsKnown(processor.E) && item.Mode != processor.E)
                    {
                        report?.ReportAt(item.Node, step, Catalogue.StateModeMismatch.Message(
                            item.Text, StateChecks.Mode(processor.E)));
                    }
                    processor = processor with { E = item.Mode };
                    break;

                case StatePart.DirectPage:
                    processor = processor with { D = SetValue(StateRegister.DirectPage, processor.D, item) };
                    break;
                case StatePart.DataBank:
                    processor = processor with { B = SetValue(StateRegister.DataBank, processor.B, item) };
                    break;
                case StatePart.AllUnknown:
                    processor = new ProcessorState(
                        Width.Unknown, Width.Unknown, ProcessorMode.Unknown, StateValue.Unknown, StateValue.Unknown);
                    break;
                default:
                    break;
            }
        }

        // Emulation mode pins both widths at 8 bits, so declaring emulation mode declares 8-bit
        // widths too.
        if (processor.E == ProcessorMode.Emulation)
        {
            if (processor.A == Width.Sixteen || processor.Index == Width.Sixteen)
                report?.Report(step, Catalogue.WidthInEmulation.Message("a 16-bit width"));
            processor = processor with { A = Width.Eight, Index = Width.Eight };
        }
        return state with { Processor = processor };

        StateValue SetValue(StateRegister register, StateValue here, StateItem item)
        {
            if (item.IsBankSet)
                return SetBanks(register, here, item);
            if (item.Expression is not { } expression)
                return StateValue.Unknown;
            if (model.ValueOf(expression, step.On).AsNumber() is not { } value)
            {
                report?.ReportAt(expression, step, Catalogue.StateValueNotConstant.Message(item.Text, register.Name));
                return StateValue.Unknown;
            }
            if (value < 0 || value > register.Maximum)
            {
                report?.ReportAt(expression, step, Catalogue.StateValueOutOfRange.Message(
                    item.Text, register.Range));
                return StateValue.Unknown;
            }
            if (here.IsBounded && !here.Values.Contains(value))
            {
                report?.ReportAt(item.Node, step,
                    Catalogue.StateValueMismatch.Message(item.Text, register.Name, here.Describe(register.Digits)));
            }
            return StateValue.Of(value);
        }

        // `.state dbr = [...]`: B is one of the banks, which it has to be already where it is known.
        StateValue SetBanks(StateRegister register, StateValue here, StateItem item)
        {
            if (register == StateRegister.DirectPage)
            {
                report?.ReportAt(item.Node, step, Catalogue.StateBanksNotDbr.Message(item.Text));
                return StateValue.Unknown;
            }
            if (item.BanksOf(expression => model.ValueOf(expression, step.On).AsNumber(), out var invalid) is not { } banks)
            {
                report?.ReportAt(invalid!, step, Catalogue.StateBanksInvalid.Message(item.Text));
                return StateValue.Unknown;
            }
            if (here.Narrowed(banks) is { } narrowed)
                return narrowed;
            report?.ReportAt(item.Node, step, Catalogue.StateValueMismatch.Message(item.Text, register.Name, here.Describe(register.Digits)));
            return StateValue.Among(banks);
        }

        Width Set(StateRegister register, Width here, StateItem item)
        {
            if (StateChecks.IsKnown(item.Width) && StateChecks.IsKnown(here) && item.Width != here)
            {
                report?.ReportAt(item.Node, step, Catalogue.StateWidthMismatch.Message(
                    item.Text, register.Name, register.Is, StateChecks.Format(here)));
            }
            return item.Width;
        }
    }

    /// <summary>
    /// Returns the state after an <c>.ensure</c> such as <c>.ensure a16, i8</c>. The widths it
    /// names hold after it, whatever it emits to make them. A 16-bit width needs native mode, and
    /// is reported unless native mode is known here.
    /// </summary>
    private static FlowState Ensured(Step step, FlowState state, StateChecks? report)
    {
        var processor = state.Processor;
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part is not (StatePart.A or StatePart.Index) || !StateChecks.IsKnown(item.Width))
            {
                report?.ReportAt(item.Node, step, Catalogue.EnsureItemNotAWidth.Message(item.Text));
                continue;
            }
            if (item.Width == Width.Sixteen && processor.E != ProcessorMode.Native)
            {
                report?.ReportAt(item.Node, step, Catalogue.EnsureNeedsNative.Message(
                    item.Text,
                    processor.E == ProcessorMode.Emulation
                        ? "the processor is in emulation mode here, where both widths are 8 bits"
                        : "the mode is not known here"));
            }
            // Emulation mode pins both widths at 8, whatever is emitted to change them.
            if (processor.E == ProcessorMode.Emulation)
                continue;
            processor = item.Part == StatePart.A
                ? processor with { A = item.Width }
                : processor with { Index = item.Width };
        }
        return state with { Processor = processor };
    }

    /// <summary>
    /// Returns the state after <c>.frame name: T</c>, in which the top <c>.sizeof(T)</c> bytes of
    /// the analysis stack become the frame. Where the stack is not known, as after <c>tcs</c>, it
    /// becomes those bytes with nothing known beneath them.
    /// </summary>
    private FlowState Framed(Step step, FrameDirectiveSyntax directive, FlowState state, StateChecks? report)
    {
        if (model.SymbolAt(directive.Name) is not { Kind: SymbolKind.Frame } frame)
            return state;
        if (directive.Type is not { } type
            || model.SymbolOf(type) is not { IsLayout: true, Size: { } size })
        {
            report?.Report(step, Catalogue.FrameNotARecord.Message(frame.DisplayName));
            return state;
        }
        if (state.Stack is not { } stack)
            return state with { Stack = AnalysisStack.OnlyFrame(frame, (int)size) };
        if (stack.Framed(frame, (int)size) is { } framed)
            return state with { Stack = framed };
        report?.Report(step, Catalogue.FramePastTheStack.Message(frame.DisplayName, size, stack.Depth));
        return state;
    }

    /// <summary>
    /// Records the stack offset of a frame member named in an operand, and reports a member that
    /// cannot be resolved. A member is only a place on the stack, so it must be a stack-relative
    /// operand, and its offset counts every byte pushed since the frame.
    /// </summary>
    private void Slot(Step step, AddressingMode? mode, FlowState state, StateChecks report)
    {
        if ((step.Statement as InstructionStatementSyntax)?.Operand is not { } operand)
            return;
        var stack = state.Stack;
        foreach (var name in operand.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            if (name.GlobalToken is not null || name.Names is not [var first, ..]
                || model.SymbolAt(first) is not { Kind: SymbolKind.Frame } frame)
            {
                continue;
            }
            var text = name.GetText().Trim();
            if (mode is not (AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
                || name.Parent is not OperandSyntax)
            {
                report.ReportAt(name, step, Catalogue.FrameMemberNotStackRelative.Message(text, text));
                continue;
            }
            if (stack is null)
            {
                report.ReportAt(name, step, Catalogue.FrameDepthUnknown.Message(text, Cause.Because(state.WhyStack)));
                continue;
            }
            if (stack.Above(frame) is not { } above)
            {
                report.ReportAt(name, step, Catalogue.FrameGone.Message(text, frame.DisplayName));
                continue;
            }
            var size = frame.TypeExpression is { } type ? model.SymbolOf(type)?.Size ?? 0 : 0;
            var offset = name.Names.Length > 1 ? model.ValueOf(name, step.On).AsNumber() ?? 0 : 0;

            // The frame's lowest byte is its last member's, and `1,s` is the byte on top.
            slots[(step.Statement.Position, step.On)] = (int)(above - size + 1 + offset + 1);
        }
    }

    /// <summary>
    /// Returns the state where an expansion of a macro with a state signature, or a block spliced
    /// into one, starts or ends. A call is checked the way <c>jsr</c> is. The state must match the
    /// entry, the body starts from it, and its end must match the exit. The state after the call
    /// is the exit with its <c>*</c> items kept from the call. A block given to such a macro has to
    /// leave the state as it found it.
    /// </summary>
    private FlowState Marked(Step step, FlowState state, StateChecks? report)
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
                report?.Report(step, Catalogue.BlockChangesState.Message(owner.DisplayName, before, processor));
            }
            return state;
        }

        if (step.Statement is not MacroCallSyntax expanded || model.MacroAt(expanded) is not { MacroSignature: { } signature } macro)
            return state;
        var name = macro.DisplayName + "!";
        if (!step.Closes)
        {
            started[key] = processor;
            report?.CheckEntry(step, $"`{name}`", signature, processor);
            return state with { Processor = signature.Entry };
        }
        report?.CheckExit(step, "", "at the end of its body", signature.Exit, processor, name);
        return state with
        {
            Processor = StateChecks.Exited(signature, started.TryGetValue(key, out var at) ? at : ProcessorState.Unknown),
        };
    }
}
