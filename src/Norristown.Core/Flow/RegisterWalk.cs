using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Follows what the registers hold through one file's routines, a routine at a time.
/// </summary>
internal sealed class RegisterWalk
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly StateAnalysis? states;
    private readonly OutsideEntries outside;

    // What reaches each block of a routine from each place it is entered, kept from the first
    // time what the routine reads is asked. It depends only on what every routine keeps,
    // which is settled by then.
    private readonly Dictionary<(FlowRegion Region, int Start), RegisterState?[]> solved = [];

    /// <summary>
    /// Initializes a walk over the routines of the file that <paramref name="flow"/> describes.
    /// <paramref name="states"/> is the file's processor-state analysis on the 65816, and null on
    /// every other CPU.
    /// </summary>
    public RegisterWalk(SemanticModel model, CodeLayout layout, ControlFlow flow, StateAnalysis? states)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        this.states = states;
        outside = new OutsideEntries(model, layout);
        Held = new RegisterStates(model.Tree);
        Effects = flow.Effects;
    }

    /// <summary>Gets what the registers hold at each statement of the file, for an editor to show.</summary>
    public RegisterStates Held { get; }

    /// <summary>
    /// Gets what each routine a call reaches leaves on the caller's stack, which the walk applies
    /// after the call. It starts as the effects the flow was composed with.
    /// </summary>
    public StackEffects Effects { get; internal set; }

    /// <summary>Gets the CPU the file the walk goes through is built for.</summary>
    internal Cpu Cpu => layout.Cpu;

    /// <summary>Gets the control flow of the file the walk goes through.</summary>
    internal ControlFlow Flow => flow;

    /// <summary>
    /// Returns the registers an instruction uses the value of, including the index register its
    /// mode adds, and the registers it certainly writes. A <c>rep</c> or <c>sep</c> whose mask
    /// nt65 cannot work out certainly writes nothing. An instruction in
    /// <see cref="ControlFlow.RewrittenOpcodes"/> uses every register and certainly writes none.
    /// </summary>
    internal (Registers Read, Registers Written) EffectsOf(Step step, InstructionStatementSyntax instruction)
    {
        if (flow.RewrittenOpcodes.Contains(step.Key))
            return (Registers.All, Registers.None);
        var mnemonic = instruction.MnemonicKind;
        var mode = layout.Of(instruction, step.On)?.Mode;
        var constant = StepOperands.Immediate(model, layout, step);
        var written = mnemonic is MnemonicKind.Rep or MnemonicKind.Sep && constant is null
            ? Registers.None
            : RegisterEffects.Written(mnemonic, mode, constant);
        return (RegisterEffects.Read(mnemonic, mode), written);
    }

    /// <summary>Gets the semantic model of the file the walk goes through.</summary>
    internal SemanticModel Model => model;

    /// <summary>Returns whether every call a block makes is one nt65 could follow into a body.</summary>
    public static bool Followed(BasicBlock block, Func<Symbol, RoutineRegisters> of) =>
        !block.CallsUnknown && block.Calls.All(callee => of(callee).Complete);

    /// <summary>
    /// Returns whether a block ends by calling a routine or by handing control to one in a tail
    /// call. Either way, what that routine keeps decides what the registers hold after the
    /// block.
    /// </summary>
    public static bool CallsAtEnd(BasicBlock block) => block.EndsInCall || block.End == BlockEnd.TailCall;

    /// <summary>
    /// Returns the state after a path passes control to a routine instead of returning. The
    /// registers that routine keeps are unchanged, and nothing is known about the rest.
    /// </summary>
    public static RegisterState Handed(RegisterState state, RoutineRegisters kept) =>
        state.WithEach(Registers.All & ~kept.Kept, RegisterValue.Unknown).Unbacking(kept.Unbacked);

    /// <summary>Returns the state the routines a block calls leave behind.</summary>
    public static RegisterState Calls(BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of)
    {
        if (block.CallsUnknown || block.Calls.Count == 0)
            return state.WithEach(Registers.All, RegisterValue.Unknown);

        // A call through a pointer whose `.next` names several routines comes back with
        // anything any one of them may have left.
        RegisterState? reached = null;
        foreach (var callee in block.Calls)
        {
            var kept = of(callee);
            reached = RegisterState.Merge(
                reached, state.WithEach(Registers.All & ~kept.Kept, RegisterValue.Unknown).Unbacking(kept.Unbacked));
        }
        return reached!;
    }

    /// <summary>Returns the step after the one at <paramref name="index"/> in a block, or null after its last.</summary>
    public static Step? NextOf(BasicBlock block, int index) =>
        index + 1 < block.Steps.Count ? block.Steps[index + 1] : null;

    /// <summary>
    /// Returns the routine a target hands control to. That is the routine itself where the
    /// target names one, and the routine a label is inside where it names a label. It is null
    /// where the target names neither, which is a target this analysis has nothing to say
    /// about.
    /// </summary>
    public static Symbol? Owner(Symbol target) =>
        target.Signature is not null ? target
            : target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner
            : null;

    /// <summary>
    /// Returns what reaches each block of <paramref name="region"/>, where the routine is entered
    /// at the block at <paramref name="start"/>, with <paramref name="of"/> giving what each
    /// routine it calls keeps. With <paramref name="fromOutside"/> false, a declared label the
    /// path from the entry does not reach is not entered at all. <paramref name="entered"/> is
    /// what the registers hold at <paramref name="start"/>, which is each one's own entry value
    /// unless it says otherwise.
    /// </summary>
    public RegisterState?[] Solve(
        FlowRegion region, Func<Symbol, RoutineRegisters> of, int start, bool fromOutside = true, RegisterState? entered = null)
    {
        var blocks = region.Blocks;
        var solver = Solver(blocks, of, block => ControlFlow.Onward(blocks, block));
        var signature = region.Routine.Signature ?? Signature.Default;
        var called = SavedStack.Entered(signature.ReturnSize, signature.Pulls);
        solver.Enter(start, (entered ?? RegisterState.Entered) with { Stack = called });

        // A label a `.state` declares, or one that another routine names, may be jumped into
        // from another routine, so the registers there hold nothing this routine put in them. The stack there is what
        // entering the routine leaves, which is empty but for the bytes a `pulls n` hands it. Code that jumps in has made none of
        // this routine's saves, so a save that the path above the label leaves on the stack
        // cannot be shown to be the one a pull below the label takes back. Entered at a label,
        // the routine's other entry points are not part of the answer unless the path from
        // that label reaches them.
        solver.EnterEntries(
            outside, declaredOnly: false, start == 0 && fromOutside ? RegisterState.Outside with { Stack = called } : null,
            (block, state) => Entered(state, called, block, region.Routine));
        return solver.Reached;
    }

    /// <summary>
    /// Returns what <see cref="Solve"/> finds reaches each block of <paramref name="region"/>
    /// entered at the block at <paramref name="start"/>, kept from the first time it is asked.
    /// It may be asked only once what every routine keeps is settled.
    /// </summary>
    public RegisterState?[] Solved(FlowRegion region, Func<Symbol, RoutineRegisters> of, int start)
    {
        if (!solved.TryGetValue((region, start), out var reached))
            solved[(region, start)] = reached = Solve(region, of, start);
        return reached;
    }

    /// <summary>
    /// Returns a solver that runs <paramref name="blocks"/> to a fixed point over what the
    /// registers hold, with <paramref name="of"/> giving what each routine they call keeps.
    /// </summary>
    public Dataflow<RegisterState> Solver(
        IReadOnlyList<BasicBlock> blocks, Func<Symbol, RoutineRegisters> of,
        Func<BasicBlock, IEnumerable<int>> successors) =>
        new(blocks, (block, state) => Through(block, state, of, null), RegisterState.Merge, successors);

    /// <summary>
    /// Returns what one block does to the registers, from the state that reaches it.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="state">The state that reaches the block.</param>
    /// <param name="of">Gives what each routine the block calls keeps.</param>
    /// <param name="report">
    /// Collects what is wrong with the block's <c>.state</c> items, and has the walk record what the
    /// registers hold at each statement for an editor. It is null on a walk that reports nothing.
    /// </param>
    /// <param name="use">
    /// Is told, where it is given, the entry values each statement uses. A store that a
    /// <c>.state saves</c> under it marks does not use the register it saves.
    /// </param>
    /// <param name="calling">
    /// Is given, where it is given, the state just before the routines the block ends by calling
    /// or handing control to take over.
    /// </param>
    public RegisterState Through(
        BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report,
        Action<Step, Registers>? use = null, Action<RegisterState>? calling = null)
    {
        var above = state;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            if (report is not null && !step.Closes)
                Held.Record(step, state);
            if (report is not null && step.Statement is StateDirectiveSyntax)
                CheckSaves(i > 0 ? block.Steps[i - 1] : null, step, above, report);
            above = state;
            var next = NextOf(block, i);
            state = use is null
                ? Step(step, state, report, next: next)
                : Step(step, state, report, entries => use(step, entries), next is { } under ? Saved(step, under, state) : Registers.None, next);
            if (i == block.Steps.Count - 1 && CallsAtEnd(block))
            {
                calling?.Invoke(state);
                state = Calls(block, state, of);
                if (block.EndsInCall)
                    state = state with { Stack = state.Stack?.AfterCall(Effects.OfCallIn(block)) };
            }
        }
        return state;
    }

    /// <summary>
    /// Returns what one statement does to the registers. <paramref name="use"/>, where it is
    /// given, is told the entry values the statement uses. <paramref name="next"/> is the step
    /// after it in its block, where there is one, which tells what the statement left the
    /// index width at.
    /// </summary>
    public RegisterState Step(
        Step step, RegisterState state, List<Diagnostic>? report, Action<Registers>? use = null,
        Registers saved = Registers.None, Step? next = null)
    {
        if (step.Statement is StateDirectiveSyntax)
            return Asserted(step, state, report);

        // An `.ensure` may emit an instruction that sets each flag it names.
        if (step.Statement is EnsureDirectiveSyntax)
            return state.WithEach(Ensured(step.Statement), RegisterValue.Written);

        // The bytes from a position inside an instruction run as the instructions they decode
        // as, one after another.
        if (layout.HiddenPathAt(step) is { } hidden)
        {
            foreach (var instruction in hidden.Instructions)
                state = Instruction(step, instruction.Mnemonic, instruction.Mode, Immediate(instruction), state, use, saved, null);
            return state with { FromStackPointer = Registers.None };
        }
        if (step.Statement is not InstructionStatementSyntax statement)
            return state;

        // A store may turn the instruction into one nothing lists, which may use every register
        // and leave anything in each.
        if (flow.RewrittenOpcodes.Contains(step.Key))
        {
            if (use is not null)
                UsedByAnything(state, use);
            return state.WithEach(Registers.All, RegisterValue.Unknown) with { FromStackPointer = Registers.None };
        }

        // A store may turn the instruction into another, and then the registers hold what either
        // leaves, and either may use them.
        var mode = layout.Of(statement, step.On)?.Mode;
        var immediate = StepOperands.Immediate(model, layout, step);
        var after = Instruction(step, statement.MnemonicKind, mode, immediate, state, use, saved, next);
        var pointing = StackWrites.Pointing(statement.MnemonicKind, mode, immediate, state.FromStackPointer);
        foreach (var variant in VariantsOf(step))
        {
            after = RegisterState.Merge(after, Instruction(step, variant, mode, immediate, state, use, saved, next));
            pointing &= StackWrites.Pointing(variant, mode, immediate, state.FromStackPointer);
        }
        return after with { FromStackPointer = pointing };
    }

    /// <summary>Returns the value of an instruction's immediate, or null where it has none.</summary>
    internal static long? Immediate(HiddenInstruction instruction) =>
        instruction.Mode == AddressingMode.Immediate ? instruction.Operand : null;

    /// <summary>
    /// Returns the instructions a store can turn <paramref name="step"/>'s instruction into, from
    /// a <c>.patch … as</c>, or an empty list where it names none.
    /// </summary>
    internal IReadOnlyList<MnemonicKind> VariantsOf(Step step) =>
        flow.Variants.TryGetValue(step.Key, out var variants) ? variants : [];

    /// <summary>
    /// Returns what the registers hold after <paramref name="step"/>'s instruction runs as
    /// <paramref name="mnemonic"/> in <paramref name="mode"/>, with <paramref name="immediate"/> as
    /// its immediate where it has one, from what they held before it.
    /// </summary>
    private RegisterState Instruction(
        Step step, MnemonicKind mnemonic, AddressingMode? mode, long? immediate, RegisterState state,
        Action<Registers>? use, Registers saved, Step? next)
    {
        var facts = Instructions.Facts(mnemonic);
        if (use is not null)
            Used(step, mnemonic, mode, state, use, saved);

        // A software interrupt runs a handler that may not even be in this program.
        if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
            return state.WithEach(Registers.All, RegisterValue.Unknown);

        // Setting the index flag zeroes the high bytes of X and Y, which `plp` and `rti` do as
        // well as `sep`, however the flag is set again later.
        if (NarrowsIndex(step, next, mnemonic))
            state = state.WithEach(Registers.X | Registers.Y, RegisterValue.Written);

        // A call is handled for the block as a whole, because its effect depends on which
        // routine it reaches.
        if (Instructions.IsCall(mnemonic))
            return state;

        // A store into the bytes on the stack may change a saved register or the flags an
        // interrupt pushed, and nt65 does not follow which byte it changes. What a pull or an
        // `rti` restores after it is then not known.
        if (facts.Stores && StackWrites.Into(model, step, mode, state.FromStackPointer))
            state = state with { Stack = null, WhyStack = Cause.StackWritten($"`{step.Statement.GetText().Trim()}`") };

        // The processor pushes the flags when it takes an interrupt, and `rti` pulls them
        // back. A handler that has left the stack where it found it therefore hands the flags
        // back unchanged, no matter how it used them on the way.
        if (mnemonic == MnemonicKind.Rti)
        {
            foreach (var flag in RegisterEffects.Each(Registers.Flags))
                state = state.With(flag, state.Stack is { Depth: 0 } ? RegisterValue.Of(flag) : RegisterValue.Unknown);
            return state;
        }

        if (facts.Pushes is { } push)
            return Saved(step, state, facts, push);

        // Nearly every instruction sets Z and N from its result, a transfer and a pull among
        // them. A `plp` gives the flags back what it pulls instead.
        var written = mnemonic == MnemonicKind.Plp
            ? Registers.None
            : RegisterEffects.Written(mnemonic, mode, immediate);
        const Registers OtherFlags = Registers.Z | Registers.N | Registers.V;
        if (facts.Pulls is { } pull)
            return Restored(step, state.WithEach(written & OtherFlags, RegisterValue.Written), facts, pull, use);

        // Moving the stack pointer leaves nothing known about the saves on the stack.
        if (RegisterEffects.SetsStackPointer(mnemonic))
            state = state with { Stack = null };

        if (RegisterEffects.Moved(mnemonic) is { } moved)
        {
            state = state.WithEach(written & OtherFlags, RegisterValue.Written);
            return moved.To == Registers.A ? Accumulator(step, state, state.Of(moved.From))
                : state.With(moved.To, moved.From == Registers.A ? Taken(step, mnemonic, state) : state.Of(moved.From));
        }
        var after = state.WithEach(written & ~Registers.A, RegisterValue.Written);
        if (!written.HasFlag(Registers.A))
            return after;

        // `xba` swaps the two halves of the accumulator. Each half still holds part of the
        // accumulator's entry value, but not the part that belongs there. `tdc` and `tsc`
        // write all 16 bits whatever the width.
        return mnemonic switch
        {
            MnemonicKind.Xba => after with
            {
                A = RegisterValue.Merge(state.AHigh, RegisterValue.Written),
                AHigh = RegisterValue.Merge(state.A, RegisterValue.Written),
            },
            MnemonicKind.Tdc or MnemonicKind.Tsc => after.With(Registers.A, RegisterValue.Written),
            _ => Accumulator(step, after, RegisterValue.Written),
        };
    }

    /// <summary>Returns the flags an <c>.ensure</c> names, as registers.</summary>
    internal static Registers Ensured(SyntaxNode ensure)
    {
        var flags = Registers.None;
        foreach (var item in StateItem.Read(ensure))
            flags |= RegisterEffects.Of(item.Flags);
        return flags;
    }

    /// <summary>
    /// Returns what a block hands control to. That is the other routines, and the labels inside
    /// them, that its jump or branch names, that a <c>.next</c> on it names in their place, or
    /// that the <c>.fallthrough</c> ending it runs into. It is also this routine's own entry,
    /// where the jump or branch names that. Control never comes back from any of them, because
    /// that routine returns to this routine's caller. The path therefore ends there, as a tail
    /// call's does.
    /// </summary>
    public IEnumerable<Symbol> Leaves(BasicBlock block, Symbol routine)
    {
        if (block.Steps.Count == 0 || block.EndsInCall || (block.End == BlockEnd.Return && block.Next is null))
            yield break;
        var step = block.Steps[^1];
        if (block.RunsInto is { } runsInto)
        {
            if (Outside(runsInto, routine))
                yield return runsInto;
            yield break;
        }
        if (block.Next is { } next)
        {
            foreach (var (symbol, _) in flow.Named(next, step.On))
            {
                if (Outside(symbol, routine))
                    yield return symbol;
            }
            yield break;
        }
        if (block.End is not (BlockEnd.Branch or BlockEnd.Jump or BlockEnd.TailCall))
            yield break;
        var mode = layout.Of(step.Statement, step.On)?.Mode;

        // A jump or a branch to the routine's own entry is a tail call to itself. The flow
        // graph does not follow it back into the routine's body, so the path ends here.
        if (Targets.Of(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Symbol is not { } target)
            yield break;
        var outside = Outside(target, routine);
        if (!outside && target.Signature is null)
            yield break;

        // A `jmp` to another routine's entry is the tail call the block already accounts for,
        // and what that routine keeps is applied there. A branch is not accounted for that
        // way, and neither is a jump into a routine's interior or to this routine's own
        // entry, so each is returned here.
        if (block.End == BlockEnd.Branch || target.Signature is null || !outside)
            yield return target;
    }

    /// <summary>
    /// Returns whether the accumulator, or the index registers where <paramref name="index"/>
    /// is true, are 16 bits wide before a step, or null where that is not known. Every CPU but
    /// the 65816 has no high byte to speak of, and there the answer is true, so that a write
    /// covers the whole register.
    /// </summary>
    public bool? Wide(Step step, bool index)
    {
        if (states is null)
            return true;
        var width = states.Before(step.Statement, step.On)?.Processor is { } processor
            ? index ? processor.Index : processor.A
            : Semantics.Width.Unknown;
        return width switch
        {
            Semantics.Width.Sixteen => true,
            Semantics.Width.Eight => false,
            _ => null,
        };
    }

    /// <summary>
    /// Determines whether a statement may make the index registers 8 bits wide when they may
    /// have been 16, which zeroes the high bytes of X and Y. A routine entered with 8-bit
    /// index registers found those bytes zero, so zeroing them again changes nothing it was
    /// given. Only an instruction that can set the index flag narrows it.
    /// </summary>
    public bool NarrowsIndex(Step step, Step? next, MnemonicKind mnemonic)
    {
        var sets = mnemonic switch
        {
            MnemonicKind.Sep => StepOperands.Immediate(model, layout, step) is not { } flags
                || (flags & (long)StatusFlags.X) != 0,
            MnemonicKind.Plp or MnemonicKind.Xce => true,
            _ => false,
        };
        return sets && states is not null
            && step.Routine?.Signature?.Entry.Index != Semantics.Width.Eight
            && Wide(step, index: true) != false
            && (next is not { } after || Wide(after, index: true) != true);
    }

    /// <summary>
    /// Returns how wide the register a push of this size moves is. Only the 65816 has widths. A
    /// routine that changes neither width reads <see cref="Semantics.Width.Unchanged"/> at both
    /// the save and the restore, so the two cancel.
    /// </summary>
    public Semantics.Width Width(Step step, PushSize size)
    {
        if (states is null || size is PushSize.OneByte or PushSize.TwoBytes)
            return Semantics.Width.Eight;
        if (states.Before(step.Statement, step.On)?.Processor is not { } processor)
            return Semantics.Width.Unknown;
        return size == PushSize.Accumulator ? processor.A : processor.Index;
    }

    /// <summary>
    /// Returns the state at a declared label that can also be entered from outside the routine.
    /// The registers are as the path from above leaves them, and its stack is merged with
    /// <paramref name="called"/>, the stack the routine is entered with. Where the path
    /// above has pushed something, the two stacks disagree and the stack becomes unknown. A pull
    /// below the label then restores nothing known.
    /// </summary>
    private static RegisterState Entered(RegisterState reached, SavedStack called, BasicBlock block, Symbol routine)
    {
        var stack = SavedStack.Merge(reached.Stack, called);
        return reached with
        {
            Stack = stack,
            WhyStack = stack is null
                ? OutsideEntries.UnknownStack(block.Label!, routine)
                : reached.WhyStack,
        };
    }

    /// <summary>
    /// Returns the state after a <c>.state keeps</c>, from which point those registers hold what
    /// the routine was entered with.
    /// </summary>
    private static RegisterState Asserted(Step step, RegisterState state, List<Diagnostic>? report)
    {
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part == StatePart.Reads && report is not null)
            {
                report.Add(new Diagnostic(step.Statement.Tree.GetSpan(item.Node.Span),
                    Catalogue.StateItemNotAPoint.Message(item.Text)));
            }
            if (item.Part != StatePart.Keeps)
                continue;
            foreach (var register in RegisterEffects.Each(item.Registers))
            {
                if (report is not null && state.Of(register).Holds(register))
                {
                    report.Add(new Diagnostic(
                        step.Statement.Tree.GetSpan(item.Node.Span),
                        Catalogue.KeepsRedundant.Message(item.Text, RegisterEffects.Format(register)))
                    {
                        Fix = step.On is null ? new DiagnosticFix(FixKind.Item, RegisterEffects.Format(register).ToLowerInvariant()) : null,
                    });
                }
                state = state.With(register, RegisterValue.Of(register));
            }
        }
        return state;
    }

    /// <summary>
    /// Returns whether a target hands control to a routine other than
    /// <paramref name="routine"/>. Another instance of the same <see cref="Family"/> does not
    /// count as another routine, because all the instances share one body in the source.
    /// </summary>
    private static bool Outside(Symbol target, Symbol routine) =>
        Owner(target) is { } owner && owner != routine && !owner.IsSiblingOf(routine);

    /// <summary>
    /// Returns the register <paramref name="store"/> saves, where a <c>.state saves</c> directly
    /// under it says the store only saves it, and none otherwise. <paramref name="before"/> is
    /// the state before the store. A <c>.state saves</c> that names a register the store does not
    /// save marks nothing, and <see cref="CheckSaves"/> reports it.
    /// </summary>
    private static Registers Saved(Step store, Step under, RegisterState before)
    {
        if (under.Statement is not StateDirectiveSyntax)
            return Registers.None;
        var saved = Registers.None;
        foreach (var item in StateItem.Read(under.Statement))
        {
            if (item.Part != StatePart.Saves)
                continue;
            if (Saving(store, item, before, out _) is not { } register)
                return Registers.None;
            saved = register;
        }
        return saved;
    }

    /// <summary>
    /// Reports each <c>saves</c> of a <c>.state</c> that does not stand directly under a store
    /// of the register it names. <paramref name="store"/> is the statement above the
    /// <c>.state</c> on the same path, if there is one, and <paramref name="before"/> the state
    /// before it.
    /// </summary>
    private static void CheckSaves(Step? store, Step state, RegisterState before, List<Diagnostic> report)
    {
        foreach (var item in StateItem.Read(state.Statement))
        {
            if (item.Part != StatePart.Saves || item.Registers == Registers.None)
                continue;
            if (Saving(store, item, before, out var why) is null)
            {
                report.Add(new Diagnostic(state.Statement.Tree.GetSpan(item.Node.Span),
                    Catalogue.SavesNotAStore.Message(item.Text, RegisterEffects.Format(item.Registers), why)));
            }
        }
    }

    /// <summary>
    /// Returns the register <paramref name="store"/> stores, where it is a store of a register
    /// that holds the value of each register <paramref name="item"/> names, or null with the
    /// reason where it is not.
    /// </summary>
    private static Registers? Saving(Step? store, StateItem item, RegisterState before, out string why)
    {
        var mnemonic = (store?.Statement as InstructionStatementSyntax)?.MnemonicKind;
        Registers? stored = mnemonic switch
        {
            MnemonicKind.Sta => Registers.A,
            MnemonicKind.Stx => Registers.X,
            MnemonicKind.Sty => Registers.Y,
            _ => null,
        };
        if (stored is not { } register)
        {
            why = "the line above it is not `sta`, `stx` or `sty`";
            return null;
        }
        foreach (var named in RegisterEffects.Each(item.Registers))
        {
            if (named != register && before.Of(register) != before.Of(named))
            {
                why = $"`{mnemonic.ToString()!.ToLowerInvariant()}` stores {RegisterEffects.Format(register)}, "
                    + $"which does not hold {RegisterEffects.Format(named)}'s value there";
                return null;
            }
        }
        why = "";
        return register;
    }

    /// <summary>
    /// Tells <paramref name="use"/> the entry values an instruction uses. That includes those in
    /// the registers it reads, other than <paramref name="saved"/>, and those in the pushes it
    /// reaches other than by pulling them. A pull that does not match the push on top is
    /// followed in <see cref="Restored"/>.
    /// </summary>
    private void Used(
        Step step, MnemonicKind mnemonic, AddressingMode? mode, RegisterState state, Action<Registers> use,
        Registers saved)
    {
        // What a software interrupt's handler uses is not known, so every value is used. A
        // store that a `.state saves` marks does not use the register it saves.
        if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
        {
            UsedByAnything(state, use);
            return;
        }
        var read = RegisterEffects.Read(mnemonic, mode) & ~saved;
        foreach (var register in RegisterEffects.Each(read))
            use((register == Registers.A ? Taken(step, mnemonic, state) : state.Of(register)).Entry);

        // Reading the stack pointer, moving it, or addressing the stack by offset reaches the
        // pushes in some way other than pulling them back in order.
        if (RegisterEffects.ReadsStackPointer(mnemonic) || RegisterEffects.SetsStackPointer(mnemonic)
            || mode is AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
        {
            use(state.Stack?.Entries ?? Registers.None);
        }
    }

    /// <summary>
    /// Tells <paramref name="use"/> every entry value that code nt65 knows nothing about may use
    /// at a point. Such code sees all of each register and every push on the stack.
    /// </summary>
    private static void UsedByAnything(RegisterState state, Action<Registers> use)
    {
        foreach (var register in RegisterEffects.Each(Registers.All))
            use(state.Whole(register).Entry);
        use(state.Stack?.Entries ?? Registers.None);
    }

    /// <summary>
    /// Returns the state after an instruction puts <paramref name="value"/> in the accumulator.
    /// On the 65816 an 8-bit accumulator's high byte is left alone. Where the width is not
    /// known, the high byte may hold either what it held or the new value.
    /// </summary>
    private RegisterState Accumulator(Step step, RegisterState state, RegisterValue value) =>
        Wide(step, index: false) switch
        {
            true => state.With(Registers.A, value),
            false => state with { A = value },
            null => state with { A = value, AHigh = RegisterValue.Merge(state.AHigh, value) },
        };

    /// <summary>
    /// Returns what an instruction takes from the accumulator. An 8-bit accumulator gives only
    /// its low byte, except to <c>xba</c>, <c>tcd</c> and <c>tcs</c>, which take all 16 bits.
    /// <c>tax</c> and <c>tay</c> take as much as the index registers hold.
    /// </summary>
    private RegisterValue Taken(Step step, MnemonicKind mnemonic, RegisterState state)
    {
        var wide = mnemonic switch
        {
            MnemonicKind.Xba or MnemonicKind.Tcd or MnemonicKind.Tcs => true,
            MnemonicKind.Tax or MnemonicKind.Tay => Wide(step, index: true),
            _ => Wide(step, index: false),
        };
        return wide == false ? state.A : state.Whole(Registers.A);
    }

    /// <summary>
    /// Returns the state after a push, which puts what the register held on the stack, or a
    /// value nothing is known about for a push of no register.
    /// </summary>
    private RegisterState Saved(Step step, RegisterState state, InstructionFacts facts, PushSize size)
    {
        var value = facts.Held switch
        {
            Registers.None => RegisterValue.Unknown,
            Registers.A => Taken(step, MnemonicKind.Pha, state),
            _ => state.Of(facts.Held),
        };
        var push = new SavedPush(value, size, Width(step, size))
        {
            Flags = facts.Held == Registers.C ? new PushedFlags(state.Z, state.N, state.V) : null,
        };
        return state with { Stack = state.Stack?.Push(push) };
    }

    /// <summary>
    /// Returns the state after a pull, in which the register it fills gets back what the
    /// matching push held.
    /// </summary>
    private RegisterState Restored(
        Step step, RegisterState state, InstructionFacts facts, PushSize size, Action<Registers>? use)
    {
        var width = Width(step, size);
        var value = state.Stack?.Pulled(size, width) ?? RegisterValue.Unknown;
        var flags = state.Stack?.PulledFlags() ?? PushedFlags.Unknown;
        var pulled = state with { Stack = state.Stack?.Pull(size, width) };

        // A pull that does not match the push on top takes bytes of pushes other than the one
        // it restores, so what they hold is used.
        if (use is not null && state.Stack is { } stack && pulled.Stack is null)
            use(stack.Entries);
        return facts.Held switch
        {
            Registers.None => pulled,
            Registers.A => Accumulator(step, pulled, value),
            Registers.C => pulled with { C = value, Z = flags.Z, N = flags.N, V = flags.V },
            _ => pulled.With(facts.Held, value),
        };
    }
}
