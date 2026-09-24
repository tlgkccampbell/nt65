using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which registers each routine returns holding the values it was entered with,
/// across the program. A 6502 programmer's first question about someone else's routine is which
/// registers survive it, and without this analysis the answer lives only in a comment.
/// <para>
/// A routine is analysed the same way as the 65816's processor state. Its blocks are run to a
/// fixed point over what each register may hold, and a save and its restore cancel through the
/// stack, so <c>pha</c> … <c>pla</c> around a call needs no annotation. What its calls do is
/// worked out alongside it, over the whole program. Every routine starts out keeping every
/// register, and registers are removed from each routine's set until nothing changes. Because
/// the sets only shrink, two routines that call each other converge rather than loop forever.
/// In that one respect this is easier than the cycle counts.
/// </para>
/// <para>
/// A restore through memory is not seen, because ruling out every store that could have reached
/// the byte would need final addresses, which only the linker knows. A <c>.state keeps a</c>
/// where the value is restored tells the analysis what it cannot see.
/// </para>
/// </summary>
public static class RegisterKeeps
{
    /// <summary>
    /// Works out what every routine of <paramref name="files"/> keeps, stores it on each region,
    /// and reports the routines that break what they promise.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Compose(IReadOnlyList<FileAnalysis> files)
    {
        var regions = new Dictionary<RoutineKey, FlowRegion>();
        var walks = new Dictionary<RoutineKey, Walk>();
        var byFile = new List<(ControlFlow Flow, Walk Walk)>();
        foreach (var file in files)
        {
            var walk = new Walk(file.Model, file.Layout, file.Flow, file.State);
            byFile.Add((file.Flow, walk));
            foreach (var region in file.Flow.Regions)
            {
                var name = RoutineKey.Of(region.Routine);
                if (regions.TryAdd(name, region))
                    walks[name] = walk;
            }
        }

        // Every routine starts out keeping everything, and each round takes away what the round
        // before it found. Nothing is ever added back, so the rounds stop.
        var found = regions.Keys.ToDictionary(name => name, _ => RoutineRegisters.Everything);
        bool moved;
        do
        {
            moved = false;
            foreach (var (name, region) in regions)
            {
                var computed = Declared(region.Routine, walks[name].Run(region, Of, null));
                var narrowed = new RoutineRegisters(
                    found[name].Kept & computed.Kept, found[name].Complete && computed.Complete);
                if (narrowed == found[name])
                    continue;
                found[name] = narrowed;
                moved = true;
            }
        }
        while (moved);

        var diagnostics = new List<Diagnostic>();
        foreach (var (name, region) in regions)
        {
            region.Registers = found[name];
            region.ScopeRegisters = walks[name].Scopes(region, Of);
            walks[name].Run(region, Of, diagnostics);
        }
        foreach (var (flow, walk) in byFile)
            flow.Registers = walk.Held;
        return Norristown.Diagnostics.Ordered(diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message)));

        // Returns what a routine keeps. That is what the walk found or, for a routine whose body
        // is not in the program, what it declares. A routine that declares more than its body
        // shows is taken at its word, so the mistake is reported at the declaration and not at
        // every call.
        //
        // A label is treated as the routine it is inside. A jump into another routine's interior
        // leaves this routine for that one, so what it hands back is what that routine hands
        // back. A jump to the routine's entry gets the same answer.
        //
        // A routine that never returns is treated as keeping every register, because no caller
        // ever sees what it leaves in them. A path that calls it or jumps into it ends there.
        RoutineRegisters Of(Symbol target)
        {
            var routine = target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner : target;
            if (routine.Signature is { NeverReturns: true })
                return RoutineRegisters.Everything;
            return found.TryGetValue(RoutineKey.Of(routine), out var known) ? known
                : routine.Signature?.Keeps is { } keeps && keeps != Registers.None ? new RoutineRegisters(keeps, true)
                : RoutineRegisters.Nothing;
        }
    }

    /// <summary>Returns what a routine keeps, with what it declares taken as kept too.</summary>
    private static RoutineRegisters Declared(Symbol routine, RoutineRegisters found) =>
        routine.Signature?.Keeps is { } keeps && keeps != Registers.None
            ? found with { Kept = found.Kept | keeps }
            : found;


    /// <summary>Follows one file's routines, a routine at a time.</summary>
    private sealed class Walk
    {
        private readonly SemanticModel model;
        private readonly CodeLayout layout;
        private readonly ControlFlow flow;
        private readonly StateAnalysis? states;
        private readonly OutsideEntries outside;

        public Walk(SemanticModel model, CodeLayout layout, ControlFlow flow, StateAnalysis? states)
        {
            this.model = model;
            this.layout = layout;
            this.flow = flow;
            this.states = states;
            outside = new OutsideEntries(model, layout);
            Held = new RegisterStates(model.Tree);
        }

        /// <summary>Gets what the registers hold at each statement of the file, for an editor to show.</summary>
        public RegisterStates Held { get; }

        /// <summary>
        /// Returns what <paramref name="region"/>'s routine keeps, with <paramref name="of"/> giving
        /// what each routine it calls keeps. <paramref name="report"/> collects what is wrong with
        /// the routine on the final walk, once the answer has reached a fixed point. Earlier rounds
        /// pass null and report nothing.
        /// </summary>
        public RoutineRegisters Run(FlowRegion region, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report)
        {
            var blocks = region.Blocks;
            if (!region.IsEntered || blocks.Count == 0)
                return RoutineRegisters.Everything;

            var solver = Solver(blocks, of, block => flow.Onward(blocks, block));
            solver.Enter(0, RegisterState.Entered);

            // A label a `.state` declares may be jumped into from another routine, so the
            // registers there hold nothing this routine put in them. The stack there is what a
            // call to the routine leaves, which is empty. Code that jumps in has made none of
            // this routine's saves, so a save that the path above the label leaves on the stack
            // cannot be shown to be the one a pull below the label takes back.
            solver.EnterDeclared(
                outside, RegisterState.Outside, (block, state) => Entered(state, block, region.Routine));

            var reached = solver.Reached;
            var kept = Registers.All;
            var complete = true;
            var leaves = false;
            foreach (var block in blocks)
            {
                if (reached[block.Index] is not { } state)
                    continue;
                var ends = Ends(block);
                if (block.CallsUnknown)
                    complete = false;
                foreach (var callee in block.Calls)
                {
                    if (!of(callee).Complete)
                        complete = false;
                }
                var after = Through(block, state, of, report);

                // A path that passes control to another routine is an exit from this one, and is
                // checked against the registers that routine returns unchanged.
                var left = false;
                foreach (var into in Leaves(block, region.Routine))
                {
                    var handed = of(into);
                    if (!handed.Complete)
                        complete = false;
                    var onExit = Handed(after, handed);
                    leaves = true;
                    left = true;
                    kept &= onExit.Kept;
                    if (report is not null)
                        Check(region, block, onExit, into, handed.Kept, report);
                }
                if (left || (!ends.Returns && !ends.Tail))
                    continue;
                leaves = true;
                kept &= after.Kept;
                if (report is not null)
                    Check(region, block, after, null, Registers.None, report);
            }

            // A routine no path leaves never returns anything to a caller, so there is nothing
            // it can fail to keep. What it does to the registers matters to no one else.
            return leaves ? new RoutineRegisters(kept, complete) : RoutineRegisters.Everything;
        }

        /// <summary>
        /// Returns which registers each inline <c>.scope</c> block of <paramref name="region"/>
        /// leaves unchanged. This is the question asked of the routine, but measured from where the
        /// scope is entered rather than where the routine was. A scope that saves a register and
        /// restores it keeps it, even where the routine around it does not.
        /// </summary>
        public IReadOnlyList<ScopeRegisters> Scopes(FlowRegion region, Func<Symbol, RoutineRegisters> of)
        {
            var found = new List<ScopeRegisters>();
            foreach (var (opener, whole) in region.Inline)
            {
                if (Within(region, whole, of) is { } kept)
                    found.Add(new ScopeRegisters(opener, kept.Kept, kept.Complete));
            }
            return found;
        }

        /// <summary>
        /// Returns the state at a declared label that can also be entered from outside the routine.
        /// The registers are as the path from above leaves them, and its stack is merged with the
        /// empty stack a call to the routine leaves. Where the path above has pushed something, the
        /// two stacks disagree and the stack becomes unknown. A pull below the label then restores
        /// nothing known.
        /// </summary>
        private static RegisterState Entered(RegisterState reached, BasicBlock block, Symbol routine)
        {
            var stack = SavedStack.Merge(reached.Stack, SavedStack.Empty);
            return reached with
            {
                Stack = stack,
                WhyStack = stack is null
                    ? OutsideEntries.UnknownStack(block.Label!, routine)
                    : reached.WhyStack,
            };
        }

        /// <summary>Returns whether every call a block makes is one nt65 could follow into a body.</summary>
        private static bool Followed(BasicBlock block, Func<Symbol, RoutineRegisters> of) =>
            !block.CallsUnknown && block.Calls.All(callee => of(callee).Complete);

        /// <summary>
        /// Reports a diagnostic where a routine's <c>keeps</c> promise does not hold at a point a
        /// path leaves it, saying what to change. <paramref name="into"/> is the routine or
        /// label the path passes control to, if any, and <paramref name="kept"/> the registers
        /// that routine returns unchanged.
        /// </summary>
        private static void Check(
            FlowRegion region, BasicBlock block, RegisterState state, Symbol? into, Registers kept,
            List<Diagnostic> report)
        {
            if (region.Routine.Signature?.Keeps is not { } promised || promised == Registers.None)
                return;
            var broken = promised & ~state.Kept;
            if (broken == Registers.None)
                return;
            var at = block.Steps.Count > 0
                ? block.Steps[^1].Statement.Tree.GetSpan(block.Steps[^1].Statement.Span)
                : region.Routine.DeclarationSpan;
            var names = RegisterEffects.Format(broken);
            var items = names.ToLowerInvariant();
            var one = RegisterEffects.Each(broken).Count() == 1;

            // This routine cannot promise registers that the routine the path passes control to
            // makes no promise about. The promise belongs on the routine whose code has to
            // honour it.
            var missing = into is not null ? broken & ~kept : Registers.None;

            // When the stack is unknown, that is why the restore could not be seen. Saying what
            // made it unknown points nearer the mistake than telling the routine to restore the
            // register again.
            var fix = missing != Registers.None
                ? Handing(into!, missing)
                : state.Stack is null && state.WhyStack is { } lost
                    ? Cause.Because(lost)
                    : $": restore {(one ? "it" : "them")} before returning, or add `.state keeps {items}` "
                        + "at the point where the entry value is restored";
            report.Add(new Diagnostic(at,
                Catalogue.KeepsBroken.Message(
                    region.Routine.DisplayName, items, names, one ? "is" : "are", fix)));
        }

        /// <summary>
        /// Returns the fix to suggest where handing control to another routine is what loses the
        /// registers. The promise goes on the routine handed to, since that is the code the
        /// register has to come back through. Control never comes back here to restore anything.
        /// </summary>
        private static string Handing(Symbol into, Registers missing)
        {
            var owner = Owner(into)!;
            var name = owner.DisplayName;
            var items = RegisterEffects.Format(missing).ToLowerInvariant();
            var one = RegisterEffects.Each(missing).Count() == 1;

            // Where the path names a label rather than the routine itself, the message gives both
            // names. One says where control went, and the other says where the promise belongs.
            var gone = owner == into
                ? $"control does not come back from `{name}`, which does not promise to keep {items}"
                : $"control does not come back from `{into.DisplayName}`, and `{name}` does not promise "
                    + $"to keep {items}";
            return $": {gone}: add `keeps {items}` to `{name}` if it preserves {(one ? "it" : "them")}, "
                + "or add `.next ?` here to end the path unchecked";
        }

        /// <summary>
        /// Returns the state after a <c>.state keeps</c>, from which point those registers hold what
        /// the routine was entered with.
        /// </summary>
        private static RegisterState Asserted(Step step, RegisterState state, List<Diagnostic>? report)
        {
            foreach (var item in StateItem.Read(step.Statement))
            {
                if (item.Part != StatePart.Keeps)
                    continue;
                foreach (var register in RegisterEffects.Each(item.Registers))
                {
                    if (report is not null && state.Of(register).Holds(register))
                    {
                        report.Add(new Diagnostic(
                            step.Statement.Tree.GetSpan(item.Node.Span),
                            Catalogue.KeepsRedundant.Message(item.Text, RegisterEffects.Format(register))));
                    }
                    state = state.With(register, RegisterValue.Of(register));
                }
            }
            return state;
        }

        /// <summary>
        /// Returns the state after a path passes control to a routine instead of returning. The
        /// registers that routine keeps are unchanged, and nothing is known about the rest.
        /// </summary>
        private static RegisterState Handed(RegisterState state, RoutineRegisters kept) =>
            state.WithEach(Registers.All & ~kept.Kept, RegisterValue.Unknown);

        /// <summary>Returns the state the routines a block calls leave behind.</summary>
        private static RegisterState Calls(BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of)
        {
            if (block.CallsUnknown || block.Calls.Count == 0)
                return state.WithEach(Registers.All, RegisterValue.Unknown);

            // A call through a pointer whose `.next` names several routines comes back with
            // anything any one of them may have left.
            RegisterState? reached = null;
            foreach (var callee in block.Calls)
            {
                var kept = of(callee).Kept;
                reached = RegisterState.Merge(reached, state.WithEach(Registers.All & ~kept, RegisterValue.Unknown));
            }
            return reached!;
        }

        /// <summary>
        /// Returns whether a target hands control to a routine other than
        /// <paramref name="routine"/>. Another instance of the same <see cref="Family"/> does not
        /// count as another routine, because all the instances share one body in the source.
        /// </summary>
        private static bool Outside(Symbol target, Symbol routine) =>
            Owner(target) is { } owner && owner != routine && !owner.IsSiblingOf(routine);

        /// <summary>
        /// Returns the routine a target hands control to. That is the routine itself where the
        /// target names one, and the routine a label is inside where it names a label. It is null
        /// where the target names neither, which is a target this analysis has nothing to say
        /// about.
        /// </summary>
        private static Symbol? Owner(Symbol target) =>
            target.Signature is not null ? target
                : target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner
                : null;

        /// <summary>
        /// Returns a solver that runs <paramref name="blocks"/> to a fixed point over what the
        /// registers hold, with <paramref name="of"/> giving what each routine they call keeps.
        /// </summary>
        private Dataflow<RegisterState> Solver(
            IReadOnlyList<BasicBlock> blocks, Func<Symbol, RoutineRegisters> of,
            Func<BasicBlock, IEnumerable<int>> successors) =>
            new(blocks, (block, state) => Through(block, state, of, null), RegisterState.Merge, successors);

        /// <summary>
        /// Returns which registers the part of a routine inside <paramref name="whole"/> leaves
        /// unchanged. It returns null where there is no single pass through that part to ask
        /// about, such as when a basic block holds part of it and part of something else. The
        /// shapes accepted are the ones a scope's cost is worked out for. The scope either lies
        /// inside one basic block or is made up of whole basic blocks.
        /// </summary>
        private RoutineRegisters? Within(FlowRegion region, TextSpan whole, Func<Symbol, RoutineRegisters> of)
        {
            var blocks = region.Blocks;
            if (ScopeShape.Of(blocks, whole) is not { } shape)
                return null;

            // The scope lies inside one block, which runs all of it. What it keeps is what its own
            // statements leave, with nothing branching in or out of the middle of them.
            if (shape.IsStraight)
                return Straight(blocks[shape.Entry], whole, of);

            var inside = shape.Inside;
            var solver = Solver(blocks, of, block => flow.Onward(blocks, block).Where(to => inside[to]));
            solver.Enter(shape.Entry, RegisterState.Entered);
            var reached = solver.Reached;

            var kept = Registers.All;
            var complete = true;
            var leaves = false;
            for (var i = 0; i < blocks.Count; i++)
            {
                if (!inside[i] || reached[i] is not { } state)
                    continue;
                complete &= Followed(blocks[i], of);

                // A block is an exit from the scope when what runs after it is outside the
                // scope. A return, or a jump to another routine, is an exit too.
                var left = Leaves(blocks[i], region.Routine).ToList();
                if (left.Count == 0 && flow.Onward(blocks, blocks[i]).All(to => inside[to])
                    && !Ends(blocks[i]).Returns)
                {
                    continue;
                }
                leaves = true;
                var after = Through(blocks[i], state, of, null);
                foreach (var into in left)
                    after = Handed(after, of(into));
                kept &= after.Kept;
            }
            return leaves ? new RoutineRegisters(kept, complete) : null;
        }

        /// <summary>
        /// Returns which registers the statements of one block that lie inside a span leave
        /// unchanged.
        /// </summary>
        private RoutineRegisters? Straight(BasicBlock block, TextSpan whole, Func<Symbol, RoutineRegisters> of)
        {
            var state = RegisterState.Entered;
            var within = false;
            var any = false;
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                if (step.On is null)
                    within = step.Statement.Position >= whole.Start && step.Statement.Position < whole.End;
                if (!within)
                    continue;
                any = true;
                state = Step(step, state, null);
                if (i == block.Steps.Count - 1 && Ends(block) is { Calls: true } or { Tail: true })
                    state = Calls(block, state, of);
            }
            return any ? new RoutineRegisters(state.Kept, Followed(block, of)) : null;
        }

        /// <summary>Returns what one block does to the registers, from the state that reaches it.</summary>
        private RegisterState Through(
            BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report)
        {
            var ends = Ends(block);
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                if (report is not null && !step.Closes)
                    Held.Record(step, state);
                state = Step(step, state, report);
                if (i == block.Steps.Count - 1 && (ends.Calls || ends.Tail))
                    state = Calls(block, state, of);
            }
            return state;
        }

        /// <summary>Returns what one statement does to the registers.</summary>
        private RegisterState Step(Step step, RegisterState state, List<Diagnostic>? report)
        {
            if (step.Statement is StateDirectiveSyntax)
                return Asserted(step, state, report);
            if (step.Statement is not InstructionStatementSyntax statement)
                return state;

            var mnemonic = statement.MnemonicKind;
            var mode = layout.Of(statement, step.On)?.Mode;
            var facts = Instructions.Facts(mnemonic);

            // A software interrupt runs a handler that may not even be in this program.
            if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
                return state.WithEach(Registers.All, RegisterValue.Unknown);

            // A call is handled for the block as a whole, because its effect depends on which
            // routine it reaches.
            if (Instructions.IsCall(mnemonic))
                return state;

            // The processor pushes the flags when it takes an interrupt, and `rti` pulls them
            // back. A handler that has left the stack where it found it therefore hands the carry
            // back unchanged, no matter how it used the carry on the way.
            if (mnemonic == MnemonicKind.Rti)
                return state.With(Registers.C, state.Stack is { Depth: 0 } ? RegisterValue.Of(Registers.C) : RegisterValue.Unknown);

            if (facts.Pushes is { } push)
                return Saved(step, state, facts, push);
            if (facts.Pulls is { } pull)
                return Restored(step, state, facts, pull);

            // Moving the stack pointer leaves nothing known about the saves on the stack.
            if (mnemonic is MnemonicKind.Txs or MnemonicKind.Tcs)
                state = state with { Stack = null };

            if (RegisterEffects.Moved(mnemonic) is { } moved)
                return state.With(moved.To, state.Of(moved.From));
            return state.WithEach(
                RegisterEffects.Written(mnemonic, mode, mode == AddressingMode.Immediate ? Constant(step) : null),
                RegisterValue.Written);
        }

        /// <summary>
        /// Returns the state after a push, which puts what the register held on the stack, or a
        /// value nothing is known about for a push of no register.
        /// </summary>
        private RegisterState Saved(Step step, RegisterState state, InstructionFacts facts, PushSize size)
        {
            var value = facts.Held == Registers.None ? RegisterValue.Unknown : state.Of(facts.Held);
            return state with { Stack = state.Stack?.Push(new SavedPush(value, size, Width(step, size))) };
        }

        /// <summary>
        /// Returns the state after a pull, in which the register it fills gets back what the
        /// matching push held.
        /// </summary>
        private RegisterState Restored(Step step, RegisterState state, InstructionFacts facts, PushSize size)
        {
            var width = Width(step, size);
            var value = state.Stack?.Pulled(size, width) ?? RegisterValue.Unknown;
            var pulled = state with { Stack = state.Stack?.Pull(size, width) };
            return facts.Held == Registers.None ? pulled : pulled.With(facts.Held, value);
        }

        /// <summary>
        /// Returns how wide the register a push of this size moves is. Only the 65816 has widths. A
        /// routine that changes neither width reads <see cref="Semantics.Width.Unchanged"/> at both
        /// the save and the restore, so the two cancel.
        /// </summary>
        private Semantics.Width Width(Step step, PushSize size)
        {
            if (states is null || size is PushSize.OneByte or PushSize.TwoBytes)
                return Semantics.Width.Eight;
            if (states.Before(step.Statement, step.On)?.Processor is not { } processor)
                return Semantics.Width.Unknown;
            return size == PushSize.Accumulator ? processor.A : processor.Index;
        }

        /// <summary>Returns how a block ends, which is by calling, jumping away for good, or returning.</summary>
        private (bool Calls, bool Tail, bool Returns) Ends(BasicBlock block)
        {
            if (block.Steps.Count == 0)
                return (false, false, false);
            var step = block.Steps[^1];
            var statement = step.Statement;
            var mode = layout.Of(statement, step.On)?.Mode;
            var transfer = Transfers.Of(statement, mode);
            var control = statement is InstructionStatementSyntax instruction
                ? Instructions.Facts(instruction.MnemonicKind).Control
                : Control.Through;
            var calls = flow.EndsInCall(block);

            // `stp` and `jam` stop the processor, so nothing ever reads what they left. `rti`
            // goes back to the code the interrupt broke into, which is exactly where the
            // registers matter.
            var returns = transfer == Transfer.Return && block.Next is null && control != Control.Stops;
            var tail = !calls && !returns && (block.Calls.Count > 0 || block.CallsUnknown);
            return (calls, tail, returns);
        }

        /// <summary>
        /// Returns what a block hands control to. That is the other routines, and the labels inside
        /// them, that its jump or branch names, that a <c>.next</c> on it names in their place, or
        /// that the <c>.fallthrough</c> ending it runs into. Control never comes back from any of
        /// them, because that routine returns to this routine's caller. The path therefore ends
        /// there, as a tail call's does.
        /// </summary>
        private IEnumerable<Symbol> Leaves(BasicBlock block, Symbol routine)
        {
            if (block.Steps.Count == 0 || Ends(block) is { Calls: true } or { Returns: true })
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
            var mode = layout.Of(step.Statement, step.On)?.Mode;
            var transfer = Transfers.Of(step.Statement, mode);
            if (transfer is not (Transfer.Jump or Transfer.Branch))
                yield break;
            if (Targets.Of(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Symbol is not { } target
                || !Outside(target, routine))
            {
                yield break;
            }

            // A `jmp` to a routine's entry is the tail call the block already accounts for, and
            // what that routine keeps is applied there. A branch is not accounted for that way,
            // and neither is a jump into a routine's interior, so both are returned here.
            if (transfer != Transfer.Jump || target.Signature is null)
                yield return target;
        }

        /// <summary>Returns the value of an immediate operand, where it is known.</summary>
        private long? Constant(Step step) =>
            (step.Statement as InstructionStatementSyntax)?.Operand?.ChildNodes
                .OfType<ExpressionSyntax>().FirstOrDefault() is { } expression
                ? model.ValueOf(expression, step.On).AsNumber()
                : null;
    }
}
