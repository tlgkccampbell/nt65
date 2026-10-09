using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Tracks the 65816's register widths, emulation flag, direct page and data bank through each
/// routine. 65816 code cannot be written without them. The widths decide how wide an immediate
/// is, and D and B decide what memory an operand reaches. The analysis stays inside one routine.
/// Each routine has a signature at entry and exit, declared or inferred as
/// <see cref="InferredSignatures"/> describes, and control either stays in the routine or goes to
/// another routine's entry. The analysis records the state at each call and each return, which
/// is what the signatures are inferred from.
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
    private readonly StackEffects effects;
    private readonly InferredSignatures signatures;
    private readonly Dictionary<Symbol, StackEffect> consumed = [];

    // The signature the analysis took for each routine, with whether its exit and its entry were
    // worked out yet.
    private readonly Dictionary<Symbol, (Signature? Signature, bool ExitKnown, bool EntrySettled)> taken = [];

    // The state at each call to a routine's entry, and what each routine's returns leave.
    private readonly List<CallState> calls = [];
    private readonly Dictionary<Symbol, ProcessorState> left = [];
    private readonly Dictionary<StepKey, FlowState> reaching = [];
    private readonly Dictionary<StepKey, int> slots = [];

    // The state at the start of each expansion of a macro with a signature, and of each block
    // spliced into one. A spliced block's end is checked against it, and a macro's exit state
    // takes its unchanged parts from it.
    private readonly Dictionary<StepKey, ProcessorState> started = [];

    // The first state in reaching for each line, built when an editor first asks. Inlay hints ask
    // about every line of a file, so scanning reaching for each one would grow with the square of
    // the file.
    private Dictionary<(SyntaxTree Tree, int Position), FlowState>? anyExpansion;

    // The block moves whose banks a `.patch` says the program writes, built when the analysis
    // first meets a block move.
    private HashSet<StepKey>? patchedMoves;

    private StateAnalysis(
        SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange> ranges, StackEffects effects,
        InferredSignatures signatures)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        this.effects = effects;
        this.signatures = signatures;
        checks = new StateChecks(model, layout, ranges, SignatureOf, signatures);
        outside = new OutsideEntries(model, layout);
    }

    /// <summary>Gets what is wrong with the widths, the mode and the calls in this file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>
    /// Gets a hint for each exported routine of the file whose bytes depend on a part of its
    /// entry that is inferred rather than declared, as <see cref="InferredExports"/> finds them.
    /// </summary>
    internal IReadOnlyList<Diagnostic> Exports { get; private set; } = [];

    /// <summary>
    /// Gets the stack effect the analysis took for each routine a call in the file reaches. The
    /// effects are worked out across the program after each file is analyzed, so a file whose
    /// effects turn out different is analyzed again with them.
    /// </summary>
    internal IReadOnlyDictionary<Symbol, StackEffect> Consumed => consumed;

    /// <summary>Gets the state at each call, tail call and run-in to a routine's entry.</summary>
    internal IReadOnlyList<CallState> Calls => calls;

    /// <summary>
    /// Gets what the returns of each routine in the file leave, merged over them, for a routine
    /// that a return was found for. A tail call returns with what the routine it calls leaves.
    /// </summary>
    internal IReadOnlyDictionary<Symbol, ProcessorState> Left => left;

    /// <summary>
    /// Gets the most times any one block was walked before its region reached a fixed point,
    /// counting the walk that found nothing had changed. This measures how quickly the analysis
    /// converges.
    /// </summary>
    public int MaximumWalks { get; private set; }

    /// <summary>
    /// Works out the processor state through every routine of <paramref name="flow"/>'s file.
    /// <paramref name="ranges"/> is the project's table of which banks each range of absolute
    /// addresses may be reached from. <paramref name="signatures"/> gives the signature each
    /// routine is analyzed with, which is what it declares where it is null.
    /// </summary>
    public static StateAnalysis Of(
        SemanticModel model, CodeLayout layout, ControlFlow flow, IReadOnlyList<Project.AccessRange>? ranges = null,
        StackEffects? effects = null, InferredSignatures? signatures = null)
    {
        var analysis = new StateAnalysis(
            model, layout, flow, ranges ?? [], effects ?? StackEffects.None, signatures ?? InferredSignatures.None);
        foreach (var region in flow.Regions)
            analysis.Analyze(region);
        analysis.checks.CheckOutsideRoutines();
        analysis.Exports = InferredExports.Of(layout, flow, analysis.signatures, analysis, analysis.EnteredWith);
        analysis.Diagnostics = Norristown.Diagnostics.Ordered(
            analysis.checks.Found.Concat(analysis.UndeclaredExports()).DistinctBy(d => (d.Span, d.Id, d.Message)));
        return analysis;
    }

    /// <summary>
    /// Returns a value indicating whether the analysis took a signature for some routine that
    /// <paramref name="current"/> gives differently, and so has to be run again with it.
    /// </summary>
    internal bool TookStale(InferredSignatures current) =>
        taken.Any(pair => !Equals(current.Of(pair.Key), pair.Value.Signature) || current.IsExitKnown(pair.Key) != pair.Value.ExitKnown
            || current.IsEntrySettled(pair.Key) != pair.Value.EntrySettled);

    /// <summary>
    /// Returns the state reaching <paramref name="statement"/> in the expansion
    /// <paramref name="on"/>, or null where nothing reaches it or it is in no routine.
    /// </summary>
    public FlowState? Before(SyntaxNode statement, Expansion? on = null) =>
        reaching.GetValueOrDefault(StepKey.Of(statement, on));

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
    /// Returns the <c>n</c> of <c>n,s</c> that the frame slot in <paramref name="statement"/>'s
    /// operand comes to in the expansion <paramref name="on"/>. It returns null where the operand
    /// names no slot or the slot is not known.
    /// </summary>
    public int? SlotAt(SyntaxNode statement, Expansion? on = null) =>
        slots.TryGetValue(StepKey.Of(statement, on), out var slot) ? slot : null;

    /// <summary>
    /// Returns the state reaching a statement in any <see cref="Expansion"/> of it. An editor asks
    /// about a line, and is shown the state that reaches the first expansion found. An expansion's
    /// line belongs to the file that contains the body it expands. For a macro declared in another
    /// file that is the other file, so the same position in two files is two lines.
    /// </summary>
    public FlowState? AnyBefore(SyntaxNode statement)
    {
        // The analysis is complete before anyone asks, so the index never goes stale. Two threads
        // that build it at once build the same one.
        var index = anyExpansion;
        if (index is null)
        {
            index = [];
            foreach (var (key, state) in reaching)
                index.TryAdd((key.On?.Body?.Tree ?? model.Tree, key.Position), state);
            anyExpansion = index;
        }
        return index.GetValueOrDefault((statement.Tree, statement.Position));
    }

    /// <summary>
    /// Returns a routine's state when it is entered. That is its entry, declared or inferred, with
    /// nothing pushed except, for a routine that takes <c>args n</c>, the arguments and the return
    /// address above them. Where the callers disagree on D, the cause names them.
    /// </summary>
    private static FlowState Entry(Signature signature, Symbol routine, InferredSignatures signatures) => new(signature.Entry, EntryStack(signature))
    {
        WhyA = signature.Entry.A == Width.Unknown ? EntryCause(signature, routine, "a?") : null,
        WhyIndex = signature.Entry.Index == Width.Unknown ? EntryCause(signature, routine, "i?") : null,
        WhyD = signature.Entry.D.Kind == StateValueKind.Unknown
            && signatures.DisagreementOn(routine, StateParts.DirectPage) is { } callers
                ? CallersDisagree(routine, callers)
                : null,
    };

    /// <summary>
    /// Returns the cause for a direct page that is unknown where a routine is entered, because
    /// its callers enter it with different ones.
    /// </summary>
    private static Cause CallersDisagree(Symbol routine, IReadOnlyList<(string State, Span At)> callers)
    {
        var states = callers.Select(caller => $"`{caller.State}`").Distinct().Order(StringComparer.Ordinal).ToList();
        return new(
            $"`{routine.DisplayName}` is called with {string.Join(" and ", states)}",
            "the `dp` it expects, in its signature, makes each caller set it");
    }

    /// <summary>
    /// Returns the analysis stack where a routine is entered. It is empty except, for a routine
    /// that takes <c>args n</c>, the arguments and the return address above them. Either way its
    /// height starts at the return address, because the arguments belong to the caller.
    /// </summary>
    private static AnalysisStack EntryStack(Signature signature) =>
        AnalysisStack.Entered(signature.ReturnSize, signature.Arguments);

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
    /// Returns <paramref name="state"/> with the widths a <c>plp</c> restores from
    /// <paramref name="saved"/>, outside emulation mode. A 16-bit width comes back only in native
    /// mode. Where the mode is not known, the processor may be in emulation mode and keep the
    /// width at 8 bits, so a saved 16 becomes unknown, as it does for <c>rep</c>. A saved
    /// <c>*</c> comes back where the mode is still as it was at entry, since the width it means
    /// was saved in that mode.
    /// </summary>
    private static ProcessorState Restored(ProcessorState state, ProcessorState saved)
    {
        return state with { A = Back(saved.A), Index = Back(saved.Index) };

        Width Back(Width width) =>
            state.E == ProcessorMode.Native || width == Width.Eight
                || (state.E == ProcessorMode.Unchanged && width == Width.Unchanged)
                ? width
                : Width.Unknown;
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
        // A jump in never laid out the frames the path above named, so none of them is kept.
        var stack = AnalysisStack.Merge(reached.Stack?.Unframed(), EntryStack(signature));
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
    /// Checks a call made with <c>per</c> and a branch, and returns the state after it, or null
    /// where no return of the routine called has been seen yet. The call is checked as
    /// <c>jsr</c> or, with a <c>phk</c> before it, as <c>jsl</c>.
    /// </summary>
    private ProcessorState? RelativelyCalled(
        Step step, MnemonicKind mnemonic, RelativeCall call, ProcessorState state, Symbol routine, StateChecks? report)
    {
        var callee = SignatureOf(call.Routine)!;
        if (callee.IsInterrupt)
            return state;
        if (report is not null)
        {
            report.CheckRelativeCall(step, mnemonic, call, callee, state);
            Entering(step, routine, call.Routine, state, report);
        }
        return signatures.IsExitKnown(call.Routine) ? StateChecks.Exited(callee, state) : null;
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
            // The flag analysis checks the flags an `.ensure` names, on every CPU.
            if (item.Part == StatePart.Flag)
                continue;
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
    /// Reports each exported label inside a routine that no <c>.state</c> declares. Another module
    /// may jump to such a label in a state this module cannot see, so the label has to declare it.
    /// </summary>
    private IEnumerable<Diagnostic> UndeclaredExports()
    {
        foreach (var block in flow.Regions.SelectMany(region => region.Blocks))
        {
            if (block is not { Label: { Kind: SymbolKind.Label, IsExported: true, ExportSpan: { } at, Routine: { } owner } label }
                || block.IsDeclared)
            {
                continue;
            }
            yield return new Diagnostic(model.Tree.GetSpan(at),
                Catalogue.ExportedEntryNotDeclared.Message(label.DisplayName, owner.DisplayName))
            {
                Fix = new DiagnosticFix(FixKind.State, At: label.DeclarationSpan),
            };
        }
    }

    /// <summary>
    /// Runs one region to a fixed point, then once more to report.
    /// </summary>
    private void Analyze(FlowRegion region)
    {
        var blocks = region.Blocks;
        var signature = SignatureOf(region.Routine) ?? Signature.Default;
        var walks = new int[blocks.Count];
        var solver = new Dataflow<FlowState>(
            blocks,
            (block, state) =>
            {
                walks[block.Index]++;
                return Walk(block, state, region, null);
            },
            FlowState.Merge,
            block => ControlFlow.Onward(blocks, block));

        if (region.IsEntered && blocks.Count > 0)
            solver.Enter(0, Entry(signature, region.Routine, signatures));

        // A label a `.state` declares is an entry point in its own right. If no path reaches
        // it, it starts from what the directive says, over an otherwise unknown state and the
        // stack a call to the routine leaves, since a jump in arrives as a call would. If some
        // path already reaches it, the directive is checked against that path. Where the label
        // can also be entered from outside the routine, only the parts the declaration gives
        // are kept, because what the paths inside leave is no promise to code that jumps in. A
        // label entered from outside with no `.state` is reported where it is entered instead,
        // because the widths there cannot be inferred from the jump.
        solver.EnterEntries(
            outside,
            declaredOnly: true,
            new FlowState(Outside(signature), EntryStack(signature)),
            (block, state) => Entered(block, state, signature, region.Routine));

        // A block reached only past a call that does not return is not walked, and nothing in it
        // is reported.
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is { IsDead: false } state)
                Walk(block, state, region, checks);
            else if (solver.Reached[block.Index] is null)
                checks.Unreached(block, region);
        }
        MaximumWalks = Math.Max(MaximumWalks, walks.DefaultIfEmpty().Max());
    }

    /// <summary>
    /// Returns the processor state reaching each statement of <paramref name="region"/> where its
    /// routine is entered with <paramref name="entry"/> in place of the entry its signature gives.
    /// Nothing is reported or recorded, so the analysis's own answers stay as they were.
    /// </summary>
    private Dictionary<StepKey, ProcessorState> EnteredWith(FlowRegion region, ProcessorState entry)
    {
        var blocks = region.Blocks;
        var signature = (SignatureOf(region.Routine) ?? Signature.Default) with { Entry = entry };
        var solver = new Dataflow<FlowState>(
            blocks, (block, state) => Walk(block, state, region, null), FlowState.Merge, block => ControlFlow.Onward(blocks, block));
        solver.Enter(0, new FlowState(entry, EntryStack(signature)));
        solver.EnterEntries(
            outside,
            declaredOnly: true,
            new FlowState(Outside(signature), EntryStack(signature)),
            (block, state) => Entered(block, state, signature, region.Routine));

        var states = new Dictionary<StepKey, ProcessorState>();
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is not { IsDead: false } state)
                continue;
            for (var i = 0; i < block.Steps.Count && !state.IsDead; i++)
            {
                var step = block.Steps[i];
                states.TryAdd(step.Key, state.Processor);
                var last = i == block.Steps.Count - 1;
                state = Through(
                    step, i > 0 ? block.Steps[i - 1] : null, last ? block.Next : null, last ? block.End : BlockEnd.Through, state,
                    region.Routine, null);
            }
        }
        return states;
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
            if (state.IsDead)
                return state;
            var step = block.Steps[i];
            if (report is not null && !step.Closes)
                reaching[step.Key] = state;
            Step? previous = i > 0 ? block.Steps[i - 1] : null;
            var last = i == block.Steps.Count - 1;
            var next = last ? block.Next : null;
            var end = last ? block.End : BlockEnd.Through;
            state = Explained(step, next, state, Through(step, previous, next, end, state, routine, report));
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
        WhyD = after.Processor.D.Kind == StateValueKind.Unknown && before.Processor.D.Kind == StateValueKind.Unknown
            ? before.WhyD
            : null,
    };

    private Cause? Why(
        Step step, NextDirectiveSyntax? next, FlowState state, Width before, Width after, Cause? inherited)
    {
        if (after != Width.Unknown)
            return null;
        // A `plp` that finds a saved status replaces the width, so what it restores is the cause
        // even where the width was already unknown.
        var restores = step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Plp }
            && state.Stack?.Top is { IsStatus: true };
        if (before == Width.Unknown && !restores)
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
            MnemonicKind.Plp when state.Stack?.Top is { IsStatus: true } && mode != ProcessorMode.Native
                => new($"{quoted} restores a 16-bit width only in native mode, and the mode is not known", "a `.state` before it declares which mode it is"),
            MnemonicKind.Plp when state.Stack?.Top is { IsStatus: true }
                => new($"{quoted} restores a width that is not known here", "an `.ensure` after it sets it"),
            MnemonicKind.Plp => new($"{quoted} pulls a status that no `php` in this routine pushed", "an `.ensure` after it sets it"),
            MnemonicKind.Xce => new($"{quoted} follows neither `clc` nor `sec`", "a `.state` after it declares what it is"),
            MnemonicKind.Rep when mode != ProcessorMode.Native && StepOperands.Constant(model, step) is not null
                => new($"{quoted} widens nothing in emulation mode, and the mode is not known", "a `.state` before it declares which mode it is"),
            MnemonicKind.Rep or MnemonicKind.Sep => new($"{quoted} changes flags nt65 cannot work out", "an `.ensure` after it sets it"),
            MnemonicKind.Jsr or MnemonicKind.Jsl when next is not null && statement.Operand is not AbsoluteOperandSyntax
                => new($"{quoted} calls through a pointer, and its `.next` names no routine", "a `.next` that names them lets their exit state flow here"),
            MnemonicKind.Jsr or MnemonicKind.Jsl => new($"{quoted} returns with it unknown", "an `.ensure` after it sets it"),
            _ => new($"{quoted} makes it unknown", "a `.state` after it declares what it is"),
        };
    }

    /// <summary>
    /// Returns what one statement does to the state. <paramref name="end"/> is how the block ends
    /// where the statement is its last, and <see cref="BlockEnd.Through"/> for every other.
    /// </summary>
    private FlowState Through(
        Step step, Step? previous, NextDirectiveSyntax? next, BlockEnd end, FlowState state, Symbol routine,
        StateChecks? report)
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
            && Targets.Of(model, into, step.On)?.Symbol is { Kind: SymbolKind.Proc, Signature: not null } runsInto
            && SignatureOf(runsInto) is { } signature)
        {
            if (report is not null)
                TailCalled(step, ".fallthrough", MnemonicKind.None, runsInto, signature, state.Processor, routine, report);
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
        report?.CheckMemory(step, mnemonic, mode, processor, state.WhyD, routine);

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

            // What a push saves is kept in the routine's terms, so a pull outside the macro body
            // that pushed it reads the same state.
            case MnemonicKind.Php:
                var status = InRoutine(processor, step.On);
                return state with { Stack = stack?.Push(new StackEntry(true, status.A, status.Index)) };
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
                return state with { Stack = stack?.PushValue(InRoutine(processor, step.On).B, 1) };
            case MnemonicKind.Phk:
                return state with { Stack = stack?.PushValue(ProgramBankAt(step, routine), 1) };
            case MnemonicKind.Phd:
                return state with { Stack = stack?.PushValue(InRoutine(processor, step.On).D, 2) };
            case MnemonicKind.Pea:
                return state with
                {
                    Stack = stack?.PushValue(StepOperands.Constant(model, step) is { } pushed ? StateValue.Of(pushed & 0xffff) : StateValue.Unknown, 2),
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
                var dataBank = Pulled(ProcessorState.Unknown with { B = stack?.PulledValue(1) ?? StateValue.Unknown }, step.On);
                return new FlowState(processor with { B = dataBank.B }, Pull(stack, 1));
            case MnemonicKind.Pld:
                var directPage = Pulled(ProcessorState.Unknown with { D = stack?.PulledValue(2) ?? StateValue.Unknown }, step.On);
                return new FlowState(processor with { D = directPage.D }, Pull(stack, 2));

            // A pull that finds the status register a `php` saved restores the widths saved
            // with it. Any other pull leaves them unknown. The emulation flag is not in it, and in
            // emulation mode both widths are 8 bits whatever is pulled.
            case MnemonicKind.Plp:
                var restored = processor.E == ProcessorMode.Emulation
                    ? processor with { A = Width.Eight, Index = Width.Eight }
                    : stack?.Top is { IsStatus: true } saved
                        ? Restored(processor, Pulled(ProcessorState.Unknown with { A = saved.A, Index = saved.Index }, step.On))
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
                    {
                        if (report is not null && SignatureOf(named) is { } callee)
                            TailCalled(step, ".next", MnemonicKind.None, named, callee, processor, routine, report);
                    }
                    return state with { Stack = Pull(stack, mnemonic == MnemonicKind.Rts ? 2 : 3) };
                }
                if (report is not null && SignatureOf(routine) is not { HasNoCaller: true })
                {
                    report.CheckReturn(step, mnemonic, processor, routine);
                    Leaving(routine, processor);
                }
                return state;

            default:
                return Transferred(step, mnemonic, mode, next, end, state, routine, report);
        }
    }

    /// <summary>
    /// Returns what a call or a jump does to the state. After a call the state becomes the called
    /// routine's exit, and a jump to a routine is checked as a tail call. <paramref name="end"/>
    /// is how the block ends where the statement is its last, and <see cref="BlockEnd.Through"/>
    /// for every other.
    /// </summary>
    private FlowState Transferred(
        Step step,
        MnemonicKind mnemonic,
        AddressingMode? mode,
        NextDirectiveSyntax? next,
        BlockEnd end,
        FlowState state,
        Symbol routine,
        StateChecks? report)
    {
        var statement = step.Statement;
        var transfer = Transfers.Of(statement, mode);
        var target = Targets.Of(model, Transfers.TargetOf(statement, mode), step.On)?.Symbol;
        if (end is BlockEnd.Call or BlockEnd.CallNeverReturns)
            return AfterCall(step, mnemonic, mode, transfer, target, next, state, routine, report);

        // The operand's target is checked even where a `.next` names other places.
        if (transfer is Transfer.Jump or Transfer.Branch && target is { Signature: not null } && SignatureOf(target) is { } callee
            && !checks.InAnotherSpace(step, target))
        {
            report?.CheckMirror(step, mode);
            if (report is not null)
                TailCalled(step, SyntaxFacts.TextOf(mnemonic), mnemonic, target, callee, state.Processor, routine, report);
        }
        else if (transfer is Transfer.Jump or Transfer.Branch && target is not null && Interior(target, routine) is { } owner)
        {
            if (target.StateDeclaration is null)
            {
                report?.Report(step, Catalogue.EntryNotDeclared.Message(target.DisplayName, owner.DisplayName),
                    new DiagnosticFix(FixKind.State, At: target.DeclarationSpan));
            }
            if (DeclaredElsewhere(target) is { } declared)
                report?.CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", new Signature(declared, declared, false), state.Processor);
            if (report is not null)
                JumpedInto(step, SyntaxFacts.TextOf(mnemonic), target, owner, state.Processor, routine, report);
        }
        if (report is not null && next is not null)
            CheckNamed(step, next, state, routine, report);

        // A `.next .return` goes back to the caller, so the state there is checked as a return's.
        if (report is not null && next?.ReturnToken is not null && end == BlockEnd.Return
            && (SignatureOf(routine) ?? Signature.Default) is { HasNoCaller: false } own)
        {
            report.CheckExit(step, "`.next .return`:", "here", own.Exit, state.Processor, routine.DisplayName, routine, own.Declared);
            Leaving(routine, state.Processor);
        }
        return state;
    }

    /// <summary>
    /// Returns what a call does to the state, whether it is made directly, as a relative call or
    /// through a pointer. <paramref name="transfer"/> is how the statement transfers control,
    /// <paramref name="target"/> is the symbol its operand names, if any, and
    /// <paramref name="routine"/> is the routine the call is made from. A call to a routine none
    /// of whose returns has been seen yet returns <see cref="FlowState.Dead"/>.
    /// </summary>
    private FlowState AfterCall(
        Step step,
        MnemonicKind mnemonic,
        AddressingMode? mode,
        Transfer transfer,
        Symbol? target,
        NextDirectiveSyntax? next,
        FlowState state,
        Symbol routine,
        StateChecks? report)
    {
        if (transfer == Transfer.Call)
        {
            report?.CheckMirror(step, mode);
            // A call to a name that is no routine has already been reported, and leaves the stack
            // alone so that the one mistake is not reported again.
            if (Called(step, mnemonic, target, state.Processor, routine, report) is not { } called)
                return FlowState.Dead;
            return Returned(step, state with { Processor = called }, target is null ? StackEffect.Balanced : EffectOf(target));
        }

        // A relative call comes back to the label after it, having pulled what the `per` and
        // any `phk` pushed.
        if (flow.RelativeCallAt(step) is { } relative)
        {
            if (RelativelyCalled(step, mnemonic, relative, state.Processor, routine, report) is not { } called)
                return FlowState.Dead;
            return Returned(step, new FlowState(called, Pull(state.Stack, relative.Pushed)), EffectOf(relative.Routine));
        }

        // An indirect call goes where its `.next` says, and it returns with what any of the
        // routines it names return with. With nothing named, nothing is known after it. With no
        // `.next` at all, that has been reported, and the state is left alone so the one
        // mistake is not reported again wherever the state is used.
        if (next is null)
            return state;
        var named = Routines(next, step.On).ToList();
        if (named.Count == 0)
            return Returned(step, state with { Processor = ProcessorState.Unknown }, StackEffect.Unknown);
        FlowState? merged = null;
        foreach (var each in named)
        {
            if (Called(step, mnemonic, each, state.Processor, routine, report) is { } called)
                merged = FlowState.Merge(merged, Returned(step, state with { Processor = called }, EffectOf(each)));
        }
        return merged ?? FlowState.Dead;
    }

    /// <summary>
    /// Returns what a call to <paramref name="callee"/> leaves on the stack, and records that the
    /// analysis took it.
    /// </summary>
    private StackEffect EffectOf(Symbol callee) => consumed[callee] = effects.Of(callee);

    /// <summary>
    /// Returns <paramref name="state"/> with the stack as the call at <paramref name="step"/> leaves
    /// it once a routine with <paramref name="effect"/> returns.
    /// </summary>
    private static FlowState Returned(Step step, FlowState state, StackEffect effect)
    {
        var stack = state.Stack?.AfterCall(effect);
        return stack is null && state.Stack is not null
            ? state with { Stack = null, WhyStack = Cause.CallLeavesUnknown($"`{step.Statement.GetText().Trim()}`") }
            : state with { Stack = stack };
    }

    /// <summary>
    /// Checks each place a <c>.next</c> names as a jump from here. A jump to a routine's start is
    /// checked as a tail call, and a jump to a label inside another routine as a jump into it.
    /// </summary>
    private void CheckNamed(Step step, NextDirectiveSyntax next, FlowState state, Symbol routine, StateChecks report)
    {
        foreach (var named in flow.Named(next, step.On).Select(named => named.Symbol))
        {
            if (named.Signature is not null && SignatureOf(named) is { } signature)
                TailCalled(step, ".next", MnemonicKind.None, named, signature, state.Processor, routine, report);
            else if (Interior(named, routine) is { } inside)
                JumpedInto(step, ".next", named, inside, state.Processor, routine, report);
        }
    }

    /// <summary>
    /// Returns the routines a <c>.next</c> names, leaving out the labels, which are handled as
    /// edges.
    /// </summary>
    private IEnumerable<Symbol> Routines(NextDirectiveSyntax next, Expansion? on) =>
        flow.Named(next, on).Select(named => named.Symbol).Where(symbol => symbol.Signature is not null);

    /// <summary>
    /// Checks a call from <paramref name="routine"/> and returns the state after it, or null where
    /// no return of the routine called has been seen yet. The state here must be what the routine
    /// expects, and becomes what it returns with, except for the parts it returns unchanged, which
    /// keep what they were.
    /// </summary>
    private ProcessorState? Called(
        Step step, MnemonicKind mnemonic, Symbol? target, ProcessorState state, Symbol routine, StateChecks? report)
    {
        // A call into another address space has been reported where it is laid out, and what
        // another processor's routine expects has no bearing on this processor's state.
        if (checks.InAnotherSpace(step, target))
            return state;
        if (target?.Signature is null || SignatureOf(target) is not { } callee)
        {
            report?.CheckCallTarget(step, mnemonic, target);

            // Nothing says what it returns with, and once the call is fixed its signature will.
            // Leaving the state alone keeps one mistake to one diagnostic.
            return state;
        }

        if (callee.IsInterrupt)
            return state;
        if (report is not null)
        {
            report.CheckCall(step, mnemonic, target, callee, state);
            Entering(step, routine, target, state, report);
        }
        return signatures.IsExitKnown(target) ? StateChecks.Exited(callee, state) : null;
    }

    /// <summary>
    /// Returns the signature <paramref name="routine"/> is analyzed with, and records that the
    /// analysis took it, so that the analysis is run again if the program's answer changes.
    /// </summary>
    private Signature? SignatureOf(Symbol routine)
    {
        var signature = signatures.Of(routine);
        if (signature is not null)
            taken[routine] = (signature, signatures.IsExitKnown(routine), signatures.IsEntrySettled(routine));
        return signature;
    }

    /// <summary>
    /// Records the state where <paramref name="caller"/> hands control to <paramref name="target"/>'s
    /// entry, with the program bank control arrives in, and reports where that bank is not the one
    /// <paramref name="target"/> declares with <c>pbr</c>.
    /// </summary>
    private void Entering(Step step, Symbol caller, Symbol target, ProcessorState state, StateChecks report)
    {
        var bank = ArrivingBank(step, caller, target);
        if (SignatureOf(target) is { Declared: var declared, ProgramBank: { IsBounded: true } needed }
            && (declared & StateParts.ProgramBank) != 0 && !bank.Meets(needed))
        {
            var register = StateRegister.ProgramBank;
            report.Report(step, Catalogue.CallStateMismatch.Message(
                $"`{step.Statement.GetText().Trim()}`",
                needed.Format(register),
                bank.IsBounded ? $"control reaches it in bank {bank.Describe(register.Digits)}" : "the bank control reaches it in is not known"));
        }
        calls.Add(new CallState(caller, target, state, step.Statement.Tree.GetSpan(step.Statement.Span), bank));
    }

    /// <summary>
    /// Returns the program bank that the code at <paramref name="step"/> in <paramref name="routine"/>
    /// runs in. That is the bank the routine is declared or inferred to run in, or the home bank of
    /// the step's segment where nothing says otherwise.
    /// </summary>
    private StateValue ProgramBankAt(Step step, Symbol routine) =>
        SignatureOf(routine)?.ProgramBank is { Kind: not StateValueKind.Unchanged } bank ? bank : checks.BankOf(step.Segment);

    /// <summary>
    /// Returns the program bank in which control reaches <paramref name="target"/> from the step.
    /// A long jump or call lands in the bank of the address it names, which is the bank a mirror
    /// address such as <c>($80 &lt;&lt; 16) | .loword(f)</c> gives, or the home bank of the target's
    /// segment. Every other transfer stays in the bank the caller runs in.
    /// </summary>
    private StateValue ArrivingBank(Step step, Symbol caller, Symbol target)
    {
        if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Jsl or MnemonicKind.Jml or MnemonicKind.Rtl })
            return ProgramBankAt(step, caller);
        var mode = layout.Of(step.Statement, step.On)?.Mode;
        return Targets.MirrorOf(model, Transfers.TargetOf(step.Statement, mode), step.On) is { } mirror && mirror.Routine == target
            ? StateValue.Of(mirror.Bank)
            : checks.BankOf(target.Segment);
    }

    /// <summary>Records the state that one of <paramref name="routine"/>'s returns leaves.</summary>
    private void Leaving(Symbol routine, ProcessorState state) =>
        left[routine] = left.TryGetValue(routine, out var earlier)
            ? FlowState.Merge(new FlowState(earlier, null), new FlowState(state, null)).Processor
            : state;

    /// <summary>
    /// Checks a tail call from <paramref name="routine"/> to <paramref name="target"/>, and records
    /// the state it is made in and, where the target returns, what it returns with.
    /// </summary>
    private void TailCalled(
        Step step, string via, MnemonicKind mnemonic, Symbol target, Signature callee, ProcessorState state, Symbol routine,
        StateChecks report)
    {
        report.CheckTailCall(step, via, mnemonic, target, callee, state, routine);
        if (callee.IsInterrupt)
            return;
        Entering(step, routine, target, state, report);
        if (SignatureOf(routine) is not { HasNoCaller: true } && !callee.NeverReturns && signatures.IsExitKnown(target))
            Leaving(routine, StateChecks.Exited(callee, state));
    }

    /// <summary>
    /// Checks a jump from <paramref name="routine"/> to a label inside <paramref name="owner"/>,
    /// and records what that routine returns with, where it returns.
    /// </summary>
    private void JumpedInto(
        Step step, string via, Symbol label, Symbol owner, ProcessorState state, Symbol routine, StateChecks report)
    {
        report.CheckJumpInto(step, via, label, owner, state, routine);
        var callee = SignatureOf(owner) ?? Signature.Default;
        if (SignatureOf(routine) is not { HasNoCaller: true } && !callee.NeverReturns && !callee.IsInterrupt && signatures.IsExitKnown(owner))
            Leaving(routine, StateChecks.Exited(callee, state));
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
    /// mode is not known, a <c>sep</c> still makes them 8, which they are in either mode. A mask
    /// that nt65 cannot work out, or that a store into the code may write, leaves both unknown.
    /// </summary>
    private ProcessorState Flags(Step step, bool reset, ProcessorState state)
    {
        // Emulation mode pins both widths at 8 regardless of the operand, so an operand nt65
        // cannot work out changes nothing there. The mode is checked first, because forgetting
        // the widths and then finding the mode would throw away what the mode already said.
        if (state.E == ProcessorMode.Emulation)
            return state;

        // A store a `.patch` acknowledges may write the mask, and the mask as written then says
        // only what the program starts from, as an operand nt65 cannot work out says nothing.
        if (flow.RewrittenOperands.Contains(step.Key) || StepOperands.Constant(model, step) is not { } flags)
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
    /// Returns the constant <paramref name="step"/> loads into A, for an <c>lda #c</c>, or null for
    /// anything else.
    /// </summary>
    private long? Loaded(Step step) =>
        step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Lda }
            ? StepOperands.Immediate(model, layout, step)
            : null;

    /// <summary>
    /// Returns the destination bank of <c>mvn #src, #dst</c>, which is where it leaves the data
    /// bank. The destination is a constant, or <c>^sym</c>, the bank of a symbol whose segment
    /// declares one. A block move that a <c>.patch</c> names leaves the data bank unknown, because
    /// the program writes its banks while it runs and the operands written in the source are only
    /// placeholders.
    /// </summary>
    private StateValue MovedTo(Step step)
    {
        patchedMoves ??= PatchedMoves();
        if (patchedMoves.Contains(step.Key)
            || StepOperands.Of(model, step) is not ImmediateOperandSyntax { SecondValue: { } destination })
            return StateValue.Unknown;
        if (model.ValueOf(destination, step.On).AsNumber() is { } bank and >= 0 and <= 0xff)
            return StateValue.Of(bank);
        return destination is UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Caret, Operand: var named }
            && Targets.Of(model, named, step.On)?.Symbol is { } symbol && checks.SegmentOf(symbol)?.Bank is { } home
                ? StateValue.Of(home)
                : StateValue.Unknown;
    }

    /// <summary>
    /// Returns the block moves in the file that stand on a label a <c>.patch</c> names, which are
    /// the ones whose banks the program writes.
    /// </summary>
    private HashSet<StepKey> PatchedMoves() =>
        [.. layout.Steps
            .Where(step => step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Mvn or MnemonicKind.Mvp }
                && flow.Patched.Contains(step.Key))
            .Select(step => step.Key)];

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
                or StatePart.Interrupt or StatePart.NoReturn or StatePart.Set or StatePart.ProgramBank)
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
                            item.Text, StateChecks.Mode(processor.E)),
                            new DiagnosticFix(FixKind.StateItem, ProcessorState.Format(processor.E)));
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
                    Catalogue.StateValueMismatch.Message(item.Text, register.Name, here.Describe(register.Digits)),
                    new DiagnosticFix(FixKind.StateItem, here.Format(register)));
            }
            return StateValue.Of(value);
        }

        // `.state dbr = [...]`: B is one of the banks, which it has to be already where it is known.
        StateValue SetBanks(StateRegister register, StateValue here, StateItem item)
        {
            if (register == StateRegister.DirectPage)
            {
                report?.ReportAt(item.Node, step, Catalogue.StateBanksNotDbr.Message(item.Text, "`dp` takes one address"));
                return StateValue.Unknown;
            }
            if (item.BanksOf(expression => model.ValueOf(expression, step.On).AsNumber(), out var invalid) is not { } banks)
            {
                report?.ReportAt(invalid!, step, Catalogue.StateBanksInvalid.Message(item.Text));
                return StateValue.Unknown;
            }
            if (here.Narrowed(banks) is { } narrowed)
                return narrowed;
            report?.ReportAt(item.Node, step, Catalogue.StateValueMismatch.Message(item.Text, register.Name, here.Describe(register.Digits)),
                new DiagnosticFix(FixKind.StateItem, here.Format(register)));
            return StateValue.Among(banks);
        }

        Width Set(StateRegister register, Width here, StateItem item)
        {
            if (StateChecks.IsKnown(item.Width) && StateChecks.IsKnown(here) && item.Width != here)
            {
                report?.ReportAt(item.Node, step, Catalogue.StateWidthMismatch.Message(
                    item.Text, register.Name, register.Is, StateChecks.Format(here)),
                    new DiagnosticFix(FixKind.StateItem, ProcessorState.Format(register, here)));
            }
            return item.Width;
        }
    }

    /// <summary>
    /// Returns the state after <c>.frame name: T</c>, in which the top <c>.sizeof(T)</c> bytes of
    /// the analysis stack become the frame. After <c>tcs</c> or <c>txs</c> the frame may reach beneath
    /// what is known. Where the stack is not known at all, as where paths that pushed different
    /// amounts meet, the frame may name the return address, so it is an error. The stack then
    /// becomes the frame's bytes with nothing known beneath them, so its slots are not reported too.
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
        {
            report?.Report(step, Catalogue.FrameStackUnknown.Message(frame.DisplayName, size, Cause.Because(state.WhyStack)));
            return state with { Stack = AnalysisStack.OnlyFrame(frame, (int)size) };
        }
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
            slots[step.Key] = (int)(above - size + 1 + offset + 1);
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
        var key = step.Key;
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

    /// <summary>
    /// Returns <paramref name="state"/>, which holds at a statement in the expansion
    /// <paramref name="on"/>, in the routine's terms. Inside the body of a macro with a signature,
    /// a <c>*</c> item means the state at the call rather than at the routine's entry. Each such
    /// item becomes the state at the call, through every enclosing call in turn. The stack keeps
    /// what is pushed in these terms, so a pull outside the body reads what the push saved.
    /// </summary>
    private ProcessorState InRoutine(ProcessorState state, Expansion? on)
    {
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Call is not { } call || model.MacroAt(call) is not { MacroSignature: not null })
                continue;
            var at = started.TryGetValue(StepKey.Of(call, level.Outer), out var found) ? found : ProcessorState.Unknown;
            state = new ProcessorState(
                state.A == Width.Unchanged ? at.A : state.A,
                state.Index == Width.Unchanged ? at.Index : state.Index,
                state.E == ProcessorMode.Unchanged ? at.E : state.E,
                state.D.IsEntered ? at.D : state.D,
                state.B.IsEntered ? at.B : state.B);
        }
        return state;
    }

    /// <summary>
    /// Returns what a pull at a statement in the expansion <paramref name="on"/> gets back from
    /// <paramref name="saved"/>, which the stack holds in the routine's terms. Inside the body of
    /// a macro with a signature, a value that is what the body's <c>*</c> item means comes back
    /// as that item. Any other value comes back where it is known, and is unknown otherwise.
    /// </summary>
    private ProcessorState Pulled(ProcessorState saved, Expansion? on)
    {
        Signature? signature = null;
        for (var level = on; level is not null && signature is null; level = level.Outer)
        {
            if (level.Call is { } call)
                signature = model.MacroAt(call)?.MacroSignature;
        }
        if (signature is null)
            return saved;
        var entry = signature.Entry;
        var meant = InRoutine(entry, on);
        return new ProcessorState(
            Back(saved.A, entry.A, meant.A),
            Back(saved.Index, entry.Index, meant.Index),
            ProcessorMode.Unknown,
            Value(saved.D, entry.D, meant.D),
            Value(saved.B, entry.B, meant.B));

        static Width Back(Width saved, Width entry, Width meant) =>
            entry == Width.Unchanged && saved == meant && saved != Width.Unknown ? Width.Unchanged
            : saved is Width.Eight or Width.Sixteen ? saved
            : Width.Unknown;

        static StateValue Value(StateValue saved, StateValue entry, StateValue meant) =>
            entry.IsEntered && saved == meant && saved.Kind != StateValueKind.Unknown ? entry
            : saved.IsBounded ? saved
            : StateValue.Unknown;
    }
}
