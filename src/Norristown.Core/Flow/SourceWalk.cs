using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Follows where the value of each register and flag was set, through one routine at a time, for
/// an editor that shows where the inputs of the instruction at the caret come from.
/// <para>
/// The <see cref="RegisterWalk"/> runs over the whole program on every analysis, and records
/// whether a register holds an entry value, a written value or neither, but not where it was
/// written. Carrying the places through its program-wide fixed point would cost every analysis for
/// a question an editor asks about one routine at a time. This walk answers that question on
/// demand instead, over the same blocks, the same solver and the same callee contracts. It follows
/// the register walk step for step, and a test holds the two to the same answer.
/// </para>
/// <para>
/// The answer is for showing and feeds no check. Branch conditions do not narrow the paths, as in
/// every other walk here, so a source counts if any path through the blocks brings it.
/// </para>
/// </summary>
internal sealed class SourceWalk
{
    /// <summary>
    /// The flags this walk follows apart from the carry, which is followed with the registers, and
    /// the value each is followed as.
    /// </summary>
    private static readonly (StatusFlags Flag, Tracked Tracked)[] Followed =
        [(StatusFlags.Negative, Tracked.N), (StatusFlags.Zero, Tracked.Z), (StatusFlags.Overflow, Tracked.V)];

    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly RegisterWalk registers;
    private readonly StateAnalysis? states;
    private readonly OutsideEntries outside;
    private readonly Func<Symbol, RoutineRegisters> of;
    private readonly StackEffects effects;

    /// <summary>
    /// Initializes a walk over the routines of the file that <paramref name="flow"/> describes.
    /// <paramref name="states"/> is the file's processor-state analysis on the 65816, and null on
    /// every other CPU. <paramref name="of"/> gives what each routine keeps, as the program-wide
    /// analysis settled it.
    /// </summary>
    public SourceWalk(
        SemanticModel model, CodeLayout layout, ControlFlow flow, StateAnalysis? states, Func<Symbol, RoutineRegisters> of)
    {
        this.model = model;
        this.layout = layout;
        this.of = of;
        this.states = states;
        effects = flow.Effects;
        registers = new RegisterWalk(model, layout, flow, states);
        outside = new OutsideEntries(model, layout, flow);
    }

    /// <summary>
    /// Gets a value indicating whether the CPU has register widths, which only the 65816 does. On
    /// every other CPU nothing sets them, and they are not reported.
    /// </summary>
    public bool HasWidths => states is not null;

    /// <summary>
    /// Returns where the value of <paramref name="register"/> that a call hands to
    /// <paramref name="callee"/> was set. A routine that declares an 8-bit accumulator on entry
    /// reads only its low byte. Any other reads both halves, as far as the walk can tell.
    /// </summary>
    public static SourceValue Given(Symbol? callee, Registers register, SourceState state) =>
        register == Registers.A && callee?.Signature?.Entry.A == Semantics.Width.Eight
            ? state.Of(Tracked.A)
            : state.Whole(register);

    /// <summary>
    /// Returns what reaches each block of <paramref name="region"/> from the routine's own entry, or
    /// null for a block nothing reaches. A label that a <c>.state</c> declares, or that can be
    /// entered from outside the routine, is treated as the <see cref="RegisterWalk"/> treats it. Where no path from the entry reaches it, everything is
    /// unknown there, with the label as the blocker.
    /// </summary>
    public SourceState?[] Solve(FlowRegion region)
    {
        var blocks = region.Blocks;
        var solver = new Dataflow<SourceState>(
            blocks, Through, SourceState.Merge, block => ControlFlow.Onward(blocks, block));
        if (blocks.Count == 0)
            return solver.Reached;
        var signature = region.Routine.Signature ?? Signature.Default;
        var called = SourceStack.Entered(signature.ReturnSize, signature.Pulls);
        solver.Enter(0, SourceState.Entered.WithStack(called));
        foreach (var block in blocks)
        {
            if (!block.IsDeclared && !outside.Reaches(block))
                continue;
            var here = solver.Reached[block.Index];
            SourceState state;
            if (here is null)
                state = SourceState.Unknown(block.Steps[0].Key, called);
            else if (outside.Reaches(block))
                state = here.WithStack(SourceStack.Merge(here.Stack, called));
            else
                continue;
            if (!state.Equals(here))
                solver.Enter(block.Index, state);
        }
        return solver.Reached;
    }

    /// <summary>
    /// Returns where each value was set just before the step at <paramref name="index"/> in
    /// <paramref name="block"/>, given the state that reaches the block.
    /// </summary>
    public SourceState Before(BasicBlock block, SourceState reached, int index)
    {
        var state = reached;
        for (var i = 0; i < index; i++)
            state = After(block, i, state);
        return state;
    }

    /// <summary>
    /// Returns where the value of <paramref name="register"/> that the instruction at
    /// <paramref name="step"/> reads was set. An instruction that reads an 8-bit accumulator reads
    /// only its low byte, and one that reads it at 16 bits reads both halves.
    /// </summary>
    public SourceValue Read(Step step, Registers register, SourceState state) =>
        register == Registers.A && step.Statement is InstructionStatementSyntax statement
            ? Taken(step, statement.MnemonicKind, state)
            : state.Of(SourceState.Track(register));

    /// <summary>Returns the addressing mode layout gave the instruction at <paramref name="step"/>, or null.</summary>
    public AddressingMode? ModeOf(Step step) => layout.Of(step.Statement, step.On)?.Mode;

    /// <summary>Returns where each value was set after one block, from the state that reaches it.</summary>
    public SourceState Through(BasicBlock block, SourceState state)
    {
        for (var i = 0; i < block.Steps.Count; i++)
            state = After(block, i, state);
        return state;
    }

    /// <summary>
    /// Returns where each value was set after the step at <paramref name="index"/> in
    /// <paramref name="block"/>. The calls a block ends with take effect after its last step.
    /// </summary>
    public SourceState After(BasicBlock block, int index, SourceState state)
    {
        var step = block.Steps[index];
        state = Step(step, state, RegisterWalk.NextOf(block, index));
        if (index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block))
            state = Calls(block, step, state);
        if (index == block.Steps.Count - 1 && block.EndsInCall)
            state = state.WithStack(state.Stack?.AfterCall(effects.OfCallIn(block)));
        return state;
    }

    /// <summary>
    /// Returns the state with each register that <paramref name="value"/> gives a value for set to
    /// it. A null value leaves the register as it was.
    /// </summary>
    private static SourceState EachRegister(SourceState state, Func<Registers, SourceValue?> value)
    {
        foreach (var register in RegisterEffects.Each(Registers.All))
        {
            if (value(register) is { } set)
                state = state.With(register, set);
        }
        return state;
    }

    /// <summary>Returns the state with every followed value set to <paramref name="value"/>.</summary>
    private static SourceState All(SourceState state, SourceValue value)
    {
        foreach (var tracked in Enum.GetValues<Tracked>())
            state = state.With(tracked, value);
        return state;
    }

    /// <summary>Returns a value <paramref name="step"/> set, which put <paramref name="value"/> in the register.</summary>
    private static SourceValue Wrote(Step step, RegisterValue value) =>
        SourceValue.Of(new Origin(SourceKind.Instruction, step.Key), value);

    /// <summary>
    /// Returns the state after a <c>.state keeps</c>, from which point those registers hold what the
    /// routine was entered with. The directive is a through step for each of them.
    /// </summary>
    private static SourceState Asserted(Step step, SourceState state)
    {
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part != StatePart.Keeps)
                continue;
            foreach (var register in RegisterEffects.Each(item.Registers))
                state = state.With(register, SourceValue.Of(Origin.Entry, RegisterValue.Of(register)).Via(step.Key));
        }
        return state;
    }

    /// <summary>
    /// Returns where each value was set after one statement. <paramref name="next"/> is the step after
    /// it in its block, where there is one, which tells what the statement left the index width at.
    /// </summary>
    private SourceState Step(Step step, SourceState state, Step? next)
    {
        // The bytes from a position inside an instruction run as the instructions they decode as,
        // and each value they write was set there.
        if (layout.HiddenPathAt(step) is { } hidden)
        {
            var wrote = Wrote(step, RegisterValue.Written);
            foreach (var decoded in hidden.Instructions)
            {
                var written = RegisterEffects.Written(decoded.Mnemonic, decoded.Mode, RegisterWalk.Immediate(decoded));
                foreach (var register in RegisterEffects.Each(written & ~Registers.A))
                    state = state.With(register, wrote);
                if (written.HasFlag(Registers.A))
                    state = Accumulator(step, state, wrote);
            }
            return states is null ? state : Widened(step, state);
        }
        var after = Flagged(step, Registered(step, state, next, null), null);

        // A store may turn the instruction into another, and then values may have been set by
        // either.
        foreach (var variant in registers.VariantsOf(step))
            after = SourceState.Merge(after, Flagged(step, Registered(step, state, next, variant), variant));
        return states is null ? after : Widened(step, after);
    }

    /// <summary>
    /// Returns where N, Z and V were set after one statement, given where every value was set after
    /// its effect on the registers. <paramref name="variant"/> is the instruction a store turned it
    /// into, or null for the instruction as written.
    /// </summary>
    private SourceState Flagged(Step step, SourceState after, MnemonicKind? variant)
    {
        if (step.Statement is not InstructionStatementSyntax statement)
            return after;
        var mnemonic = variant ?? statement.MnemonicKind;
        if (mnemonic is MnemonicKind.Plp or MnemonicKind.Rti or MnemonicKind.Brk or MnemonicKind.Cop || Instructions.IsCall(mnemonic))
            return after;

        // N, Z and V are set by whatever instruction last wrote them. `plp` and `rti` restore
        // them, a software interrupt loses them, and a call sets them, all of which are handled
        // with the registers.
        var flags = FlagEffects.Written(mnemonic, layout.Of(statement, step.On)?.Mode, StepOperands.Immediate(model, layout, step));
        var wrote = Wrote(step, RegisterValue.Written);
        foreach (var (flag, tracked) in Followed)
        {
            if (flags.HasFlag(flag))
                after = after.With(tracked, wrote);
        }
        return after;
    }

    /// <summary>
    /// Returns where the 65816's widths were set after one statement, following the rules of
    /// <see cref="StateAnalysis"/>. A <c>rep</c>, a <c>sep</c>, an <c>xce</c>, a <c>plp</c>, an
    /// <c>.ensure</c> and a <c>.state</c> set the widths they name at their own line. A macro with a
    /// state signature sets the widths its signature does not leave unchanged, at its call.
    /// </summary>
    private SourceState Widened(Step step, SourceState state)
    {
        var (a, index, kind) = WidthsSet(step);
        var set = SourceValue.Of(new Origin(kind, step.Key), RegisterValue.Written);
        if (a)
            state = state.With(Tracked.M, set);
        if (index)
            state = state.With(Tracked.Index, set);
        return state;
    }

    /// <summary>
    /// Returns which widths a statement sets, and the kind of source it is for them. In emulation
    /// mode a <c>rep</c> or a <c>sep</c> changes nothing, because both widths are pinned at 8 bits.
    /// </summary>
    private (bool A, bool Index, SourceKind Kind) WidthsSet(Step step)
    {
        switch (step.Statement)
        {
            case StateDirectiveSyntax or EnsureDirectiveSyntax:
                var (a, index) = (false, false);
                foreach (var item in StateItem.Read(step.Statement))
                {
                    var all = item.Part == StatePart.AllUnknown || (item.Part == StatePart.E && item.Mode == ProcessorMode.Emulation);
                    a |= all || item.Part == StatePart.A;
                    index |= all || item.Part == StatePart.Index;
                }
                return (a, index, SourceKind.Instruction);

            case MacroCallSyntax call when model.MacroAt(call) is { MacroSignature: { } signature }:
                var widths = step.Closes ? signature.Exit : signature.Entry;
                return (widths.A != Semantics.Width.Unchanged, widths.Index != Semantics.Width.Unchanged, SourceKind.Macro);

            case InstructionStatementSyntax statement:
                switch (statement.MnemonicKind)
                {
                    case MnemonicKind.Rep or MnemonicKind.Sep:
                        if (states?.Before(step.Statement, step.On)?.Processor.E == ProcessorMode.Emulation)
                            return (false, false, SourceKind.Instruction);
                        if (StepOperands.Constant(model, step) is not { } flags)
                            return (true, true, SourceKind.Instruction);
                        return ((flags & (long)StatusFlags.M) != 0, (flags & (long)StatusFlags.X) != 0, SourceKind.Instruction);
                    case MnemonicKind.Xce or MnemonicKind.Plp:
                        return (true, true, SourceKind.Instruction);
                    default:
                        return (false, false, SourceKind.Instruction);
                }

            default:
                return (false, false, SourceKind.Instruction);
        }
    }

    /// <summary>
    /// Returns where each register's value was set after one statement, and where the flags were
    /// set after a statement that restores or loses them. <paramref name="next"/> is the step after
    /// it in its block, where there is one, and <paramref name="variant"/> the instruction a store
    /// turned it into, or null for the instruction as written.
    /// </summary>
    private SourceState Registered(Step step, SourceState state, Step? next, MnemonicKind? variant)
    {
        if (step.Statement is StateDirectiveSyntax)
            return Asserted(step, state);

        // An `.ensure` may emit an instruction that sets each flag it names.
        if (step.Statement is EnsureDirectiveSyntax)
        {
            var set = Wrote(step, RegisterValue.Written);
            foreach (var flag in RegisterEffects.Each(RegisterWalk.Ensured(step.Statement)))
                state = state.With(flag, set);
            return state;
        }
        if (step.Statement is not InstructionStatementSyntax statement)
            return state;

        var mnemonic = variant ?? statement.MnemonicKind;
        var mode = layout.Of(statement, step.On)?.Mode;
        var facts = Instructions.Facts(mnemonic);

        // A software interrupt runs a handler that may not even be in this program.
        if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
            return All(state, SourceValue.Unknown(step.Key));

        // Setting the index flag zeroes the high bytes of X and Y.
        if (registers.NarrowsIndex(step, next, mnemonic))
        {
            var zeroed = Wrote(step, RegisterValue.Written);
            state = state.With(Registers.X, zeroed).With(Registers.Y, zeroed);
        }

        // A call is handled for the block as a whole, because its effect depends on which routine
        // it reaches.
        if (Instructions.IsCall(mnemonic))
            return state;

        // A handler that has left the stack where it found it hands the carry back unchanged,
        // because `rti` pulls the flags the interrupt pushed.
        if (mnemonic == MnemonicKind.Rti)
        {
            foreach (var flag in SourceState.Flags)
            {
                state = state.With(flag, state.Stack is { IsEmpty: true }
                    ? SourceValue.Of(Origin.Entry, SourceState.EntryValue(flag)).Via(step.Key)
                    : SourceValue.Unknown(step.Key));
            }
            return state;
        }

        if (facts.Pushes is { } push)
            return Saved(step, state, facts, push);
        if (facts.Pulls is { } pull)
            return Restored(step, state, facts, pull);

        // Moving the stack pointer leaves nothing known about the saves on the stack.
        if (RegisterEffects.SetsStackPointer(mnemonic))
            state = state.WithStack(null);

        // A transfer is where the register it fills was set, so it is the source. What it puts
        // there is what the register it copies held, which is what a test compares with the
        // register walk.
        if (RegisterEffects.Moved(mnemonic) is { } moved)
        {
            var copied = Wrote(step, (moved.From == Registers.A ? Taken(step, mnemonic, state) : state.Of(SourceState.Track(moved.From))).Value);
            return moved.To == Registers.A ? Accumulator(step, state, copied) : state.With(moved.To, copied);
        }

        var written = RegisterEffects.Written(mnemonic, mode, StepOperands.Immediate(model, layout, step));
        var wrote = Wrote(step, RegisterValue.Written);
        var after = state;
        foreach (var register in RegisterEffects.Each(written & ~Registers.A))
            after = after.With(register, wrote);
        if (!written.HasFlag(Registers.A))
            return after;

        // `xba` swaps the two halves of the accumulator, and `tdc` and `tsc` write all 16 bits
        // whatever the width.
        return mnemonic switch
        {
            MnemonicKind.Xba => after
                .With(Tracked.A, Wrote(step, RegisterValue.Merge(state.Of(Tracked.AHigh).Value, RegisterValue.Written)))
                .With(Tracked.AHigh, Wrote(step, RegisterValue.Merge(state.Of(Tracked.A).Value, RegisterValue.Written))),
            MnemonicKind.Tdc or MnemonicKind.Tsc => after.With(Registers.A, wrote),
            _ => Accumulator(step, after, wrote),
        };
    }

    /// <summary>
    /// Returns where each value was set after the routines a block ends by calling, or hands control
    /// to, have run. A register a routine keeps passes through the call unchanged, and the call is
    /// a through step for it. Any other register was set by the call. Where nt65 cannot follow the
    /// call, or the routine's contract is incomplete, the call is where the analysis lost track.
    /// </summary>
    private SourceState Calls(BasicBlock block, Step step, SourceState state)
    {
        var called = SourceValue.Of(new Origin(SourceKind.Call, step.Key), RegisterValue.Unknown);
        if (block.CallsUnknown || block.Calls.Count == 0)
            return EachRegister(state, _ => SourceValue.Unknown(step.Key));

        // A call through a pointer whose `.next` names several routines brings back what any one
        // of them may have left.
        SourceState? reached = null;
        foreach (var callee in block.Calls)
        {
            var contract = of(callee);
            var left = EachRegister(state, register =>
                contract.Kept.HasFlag(register) ? null
                : contract.Complete ? called
                : SourceValue.Unknown(step.Key));
            foreach (var register in RegisterEffects.Each(contract.Kept))
            {
                left = register == Registers.A
                    ? left.With(Tracked.A, state.Of(Tracked.A).Via(step.Key)).With(Tracked.AHigh, state.Of(Tracked.AHigh).Via(step.Key))
                    : left.With(register, state.Of(SourceState.Track(register)).Via(step.Key));
            }

            // A call returns with the widths its routine's signature gives on exit, and with the
            // ones it leaves unchanged as they were. A routine with no signature, or an interrupt
            // handler, leaves the widths alone, as the processor-state analysis takes it to.
            if (states is not null && callee.Signature is { IsInterrupt: false } signature)
            {
                var emulation = signature.Exit.E == ProcessorMode.Emulation;
                left = left
                    .With(Tracked.M, emulation || signature.Exit.A != Semantics.Width.Unchanged ? called : state.Of(Tracked.M).Via(step.Key))
                    .With(Tracked.Index, emulation || signature.Exit.Index != Semantics.Width.Unchanged ? called : state.Of(Tracked.Index).Via(step.Key));
            }
            reached = SourceState.Merge(reached, left);
        }
        return reached!;
    }

    /// <summary>
    /// Returns the state after an instruction puts <paramref name="value"/> in the accumulator. On
    /// the 65816 an 8-bit accumulator's high byte is left alone. Where the width is not known, the
    /// high byte may hold either what it held or the new value.
    /// </summary>
    private SourceState Accumulator(Step step, SourceState state, SourceValue value) =>
        registers.Wide(step, index: false) switch
        {
            true => state.With(Registers.A, value),
            false => state.With(Tracked.A, value),
            null => state.With(Tracked.A, value).With(Tracked.AHigh, SourceValue.Merge(state.Of(Tracked.AHigh), value)),
        };

    /// <summary>
    /// Returns where what an instruction takes from the accumulator was set. An 8-bit accumulator
    /// gives only its low byte, except to <c>xba</c>, <c>tcd</c> and <c>tcs</c>, which take all
    /// 16 bits. <c>tax</c> and <c>tay</c> take as much as the index registers hold.
    /// </summary>
    private SourceValue Taken(Step step, MnemonicKind mnemonic, SourceState state)
    {
        var wide = mnemonic switch
        {
            MnemonicKind.Xba or MnemonicKind.Tcd or MnemonicKind.Tcs => true,
            MnemonicKind.Tax or MnemonicKind.Tay => registers.Wide(step, index: true),
            _ => registers.Wide(step, index: false),
        };
        return wide == false ? state.Of(Tracked.A) : state.Whole(Registers.A);
    }

    /// <summary>
    /// Returns the state after a push, which saves where the value it moves was set. A <c>php</c>
    /// saves every flag, and a push of anything else saves nothing that is followed.
    /// </summary>
    private SourceState Saved(Step step, SourceState state, InstructionFacts facts, PushSize size)
    {
        System.Collections.Immutable.ImmutableArray<SourceValue> values = facts.Held switch
        {
            Registers.None => [],
            Registers.A => [Taken(step, MnemonicKind.Pha, state)],
            Registers.C => [.. SourceState.Flags.Select(state.Of)],
            _ => [state.Of(SourceState.Track(facts.Held))],
        };
        return state.WithStack(state.Stack?.Push(new SourcePush(values, size, registers.Width(step, size))));
    }

    /// <summary>
    /// Returns the state after a pull. Where it matches the push on top, the register it fills gets
    /// back where the pushed value was set, with the pull as a through step. Otherwise the pull is
    /// where the analysis lost track of the value.
    /// </summary>
    private SourceState Restored(Step step, SourceState state, InstructionFacts facts, PushSize size)
    {
        var width = registers.Width(step, size);
        var push = state.Stack?.Pulled(size, width);
        var pulled = state.WithStack(state.Stack?.Pull(size, width));
        SourceValue Back(int i) =>
            push is not null && i < push.Values.Length ? push.Values[i].Via(step.Key) : SourceValue.Unknown(step.Key);
        switch (facts.Held)
        {
            case Registers.None:
                return pulled;
            case Registers.A:
                return Accumulator(step, pulled, Back(0));
            case Registers.C:
                if (push is not null && push.Values.Length != SourceState.Flags.Length)
                    push = null;
                for (var i = 0; i < SourceState.Flags.Length; i++)
                    pulled = pulled.With(SourceState.Flags[i], Back(i));
                return pulled;
            default:
                return pulled.With(facts.Held, Back(0));
        }
    }
}
