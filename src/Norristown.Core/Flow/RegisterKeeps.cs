using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Which registers each routine returns holding the values it was entered with, worked out
/// across the program. A 6502 programmer's first question about someone else's routine is which
/// registers survive it, and without this analysis the answer lives only in a comment.
/// <para>
/// A routine is analysed the same way as the 65816's processor state: its blocks are run to a
/// fixed point over what each register may hold, and a save and its restore cancel through the
/// stack, so <c>pha</c> … <c>pla</c> around a call needs no annotation. What its calls do is
/// worked out alongside it, over the whole program: every routine starts out keeping every
/// register, and registers are removed from each routine's set until nothing changes. Because
/// the sets only shrink, two routines that call each other converge rather than loop for ever,
/// which is the one respect in which this is easier than the cycle counts.
/// </para>
/// <para>
/// A restore through memory is not seen, because ruling out every store that could have reached
/// the byte would need final addresses, which only the linker knows. Writing
/// <c>.state keeps a</c> where the value is restored tells the analysis what it cannot see.
/// </para>
/// </summary>
public static class RegisterKeeps
{
    /// <summary>
    /// Works out what every routine of <paramref name="flows"/> keeps, writes it on each region,
    /// and reports the routines that break what they promise. The lists are parallel, with one
    /// file at each index, and <paramref name="states"/> is empty on the CPUs that have no
    /// register widths to follow.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Compose(
        IReadOnlyList<SemanticModel> models, IReadOnlyList<CodeLayout> layouts,
        IReadOnlyList<ControlFlow> flows, IReadOnlyList<StateAnalysis> states)
    {
        var regions = new Dictionary<(string Path, string Name), FlowRegion>();
        var walks = new Dictionary<(string Path, string Name), Walk>();
        var byFile = new List<Walk>();
        for (var i = 0; i < flows.Count && i < models.Count && i < layouts.Count; i++)
        {
            var walk = new Walk(models[i], layouts[i], flows[i], i < states.Count ? states[i] : null);
            byFile.Add(walk);
            foreach (var region in flows[i].Regions)
            {
                var name = Named(region.Routine);
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
        for (var i = 0; i < byFile.Count; i++)
            flows[i].Registers = byFile[i].Held;
        return Norristown.Diagnostics.Ordered(diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message)));

        // What a routine keeps: what the walk found, or, for one whose body is not here, what
        // it declares. A routine that declares more than its body shows is taken at its word,
        // so the mistake is reported where it is written and not at every call.
        //
        // A label is treated as the routine it is inside: a jump into another routine's
        // interior leaves this routine for that one, so what it hands back is whatever that
        // routine hands back, the same answer a jump to the routine's entry gets.
        //
        // A routine that never returns hands nothing back to anyone, so it keeps everything:
        // a path that calls it or jumps into it ends there, with no caller left to disappoint.
        RoutineRegisters Of(Symbol target)
        {
            var routine = target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner : target;
            if (routine.Signature is { NeverReturns: true })
                return RoutineRegisters.Everything;
            return found.TryGetValue(Named(routine), out var known) ? known
                : routine.Signature?.Keeps is { } keeps && keeps != Registers.None ? new RoutineRegisters(keeps, true)
                : RoutineRegisters.Nothing;
        }
    }

    /// <summary>What a routine keeps, with what it declares taken as kept too.</summary>
    private static RoutineRegisters Declared(Symbol routine, RoutineRegisters found) =>
        routine.Signature?.Keeps is { } keeps && keeps != Registers.None
            ? found with { Kept = found.Kept | keeps }
            : found;

    /// <summary>
    /// A routine identified by its file and its flattened name, rather than by its symbol: an
    /// analysis that kept a file's results from before an edit holds a different symbol object
    /// for the same routine. It is the name and not the position, because a kept file still
    /// refers to the routines of an edited file at the positions they had before the edit.
    /// </summary>
    private static (string Path, string Name) Named(Symbol routine) => (routine.Tree.Path, routine.FlatName);

    /// <summary>One file's routines, followed a routine at a time.</summary>
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

        /// <summary>What the registers hold at each statement of the file, for an editor to show.</summary>
        public RegisterStates Held { get; }

        /// <summary>
        /// What <paramref name="region"/>'s routine keeps, with <paramref name="of"/> saying what
        /// each routine it calls keeps. <paramref name="report"/> collects what is wrong with it
        /// on the final walk, once the answer has settled; earlier rounds pass null and report
        /// nothing.
        /// </summary>
        public RoutineRegisters Run(FlowRegion region, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report)
        {
            var blocks = region.Blocks;
            if (!region.IsEntered || blocks.Count == 0)
                return RoutineRegisters.Everything;

            var reached = new RegisterState?[blocks.Count];
            var pending = new SortedSet<int>();
            reached[0] = RegisterState.Entered;
            pending.Add(0);
            Settle();

            // A label a `.state` declares may be jumped into from another routine, so the
            // registers there hold nothing this routine put in them. The stack there is what a
            // call to the routine leaves, which is empty: code that jumps in has made none of
            // this routine's saves, so a save the path above the label carries past it cannot
            // be shown to be the one a pull below the label takes back.
            foreach (var block in blocks)
            {
                if (!block.IsDeclared)
                    continue;
                RegisterState entered;
                if (reached[block.Index] is not { } state)
                    entered = RegisterState.Outside;
                else if (outside.Reaches(block))
                    entered = Entered(state, block, region.Routine);
                else
                    continue;
                if (entered.Equals(reached[block.Index]))
                    continue;
                reached[block.Index] = entered;
                pending.Add(block.Index);
                Settle();
            }

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
                    var carried = Handed(after, handed);
                    leaves = true;
                    left = true;
                    kept &= carried.Kept;
                    if (report is not null)
                        Check(region, block, carried, into, handed.Kept, report);
                }
                if (left || (!ends.Returns && !ends.Tail))
                    continue;
                leaves = true;
                kept &= after.Kept;
                if (report is not null)
                    Check(region, block, after, null, Registers.None, report);
            }

            // A routine no path leaves never returns anything to a caller, so there is nothing
            // it can fail to keep; what it does to the registers matters to no one else.
            return leaves ? new RoutineRegisters(kept, complete) : RoutineRegisters.Everything;

            void Settle()
            {
                while (pending.Count > 0)
                {
                    var index = pending.Min;
                    pending.Remove(index);
                    var after = Through(blocks[index], reached[index]!, of, null);
                    foreach (var edge in Carried(blocks[index]))
                    {
                        var merged = RegisterState.Merge(reached[edge], after);
                        if (merged.Equals(reached[edge]))
                            continue;
                        reached[edge] = merged;
                        pending.Add(edge);
                    }
                }
            }

            IEnumerable<int> Carried(BasicBlock block) => CarriedTo(blocks, block);
        }

        /// <summary>
        /// The state at a declared label that can also be entered from outside the routine: the
        /// registers as the path from above leaves them, with its stack merged with the empty
        /// stack a call to the routine leaves. Where the path above has pushed something, the
        /// two stacks disagree, the stack becomes unknown, and a pull below the label restores
        /// nothing known.
        /// </summary>
        private static RegisterState Entered(RegisterState reached, BasicBlock block, Symbol routine)
        {
            var stack = SavedStack.Merge(reached.Stack, SavedStack.Empty);
            return reached with
            {
                Stack = stack,
                WhyStack = stack is null
                    ? OutsideEntries.Carried(block.Label!, routine)
                    : reached.WhyStack,
            };
        }

        /// <summary>
        /// The blocks the state after a block flows to, as the 65816's analysis treats them: after
        /// a call, flow goes on at the statement after it, and a jump to a routine's entry leaves
        /// this routine.
        /// </summary>
        private IEnumerable<int> CarriedTo(IReadOnlyList<BasicBlock> blocks, BasicBlock block)
        {
            var calls = Ends(block).Calls;
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
        /// Which registers each inline <c>.scope</c> block of <paramref name="region"/> leaves
        /// unchanged. This is the question asked of the routine, but measured from where the
        /// scope is entered rather than where the routine was: a scope that saves a register and
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
        /// Which registers the part of a routine written inside <paramref name="whole"/> leaves
        /// unchanged, or null where there is no single pass through it to ask about, such as
        /// when a basic block holds part of it and part of something else. The shapes accepted
        /// are the ones a scope's cost is worked out for: the scope lies inside one basic block,
        /// or is made up of whole basic blocks.
        /// </summary>
        private RoutineRegisters? Within(FlowRegion region, TextSpan whole, Func<Symbol, RoutineRegisters> of)
        {
            var blocks = region.Blocks;
            var held = ControlFlow.Held(blocks, whole);
            var inside = new bool[blocks.Count];
            var all = 0;
            var part = 0;
            for (var i = 0; i < blocks.Count; i++)
            {
                inside[i] = held[i].Inside > 0;
                if (held[i].Inside == 0)
                    continue;
                if (held[i].Inside == held[i].Total)
                    all++;
                else
                    part++;
            }

            // Written inside one block, which runs all of it: what it keeps is what its own
            // statements leave, with nothing branching in or out of the middle of them.
            if (part == 1 && all == 0)
            {
                var at = Array.FindIndex(held, block => block.Inside > 0);
                return blocks[at].IsReached ? Straight(blocks[at], whole, of) : null;
            }
            if (part > 0 || all == 0)
                return null;

            var entry = Array.FindIndex(inside, block => block);
            if (!blocks[entry].IsReached || inside.All(block => block))
                return null;

            var reached = new RegisterState?[blocks.Count];
            var pending = new SortedSet<int> { entry };
            reached[entry] = RegisterState.Entered;
            while (pending.Count > 0)
            {
                var index = pending.Min;
                pending.Remove(index);
                var after = Through(blocks[index], reached[index]!, of, null);
                foreach (var edge in CarriedTo(blocks, blocks[index]).Where(to => inside[to]))
                {
                    var merged = RegisterState.Merge(reached[edge], after);
                    if (merged.Equals(reached[edge]))
                        continue;
                    reached[edge] = merged;
                    pending.Add(edge);
                }
            }

            var kept = Registers.All;
            var complete = true;
            var leaves = false;
            for (var i = 0; i < blocks.Count; i++)
            {
                if (!inside[i] || reached[i] is not { } state)
                    continue;
                complete &= Followed(blocks[i], of);

                // A block is an exit from the scope when what runs after it is outside the
                // scope; a return, or a jump to another routine, is an exit too.
                var left = Leaves(blocks[i], region.Routine).ToList();
                if (left.Count == 0 && CarriedTo(blocks, blocks[i]).All(to => inside[to])
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

        /// <summary>Which registers the statements of one block written inside a span leave unchanged.</summary>
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

        /// <summary>Whether every call a block makes is one nt65 could follow into a body.</summary>
        private static bool Followed(BasicBlock block, Func<Symbol, RoutineRegisters> of) =>
            !block.CallsUnknown && block.Calls.All(callee => of(callee).Complete);

        /// <summary>
        /// Checks whether a routine's <c>keeps</c> promise holds where a path leaves it, and
        /// reports what to write when it does not. <paramref name="into"/> is the routine or
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
            var names = RegisterEffects.Spell(broken);
            var items = names.ToLowerInvariant();
            var one = RegisterEffects.Each(broken).Count() == 1;

            // Registers that the routine the path passes control to makes no promise about
            // cannot be promised by this routine either, and the promise belongs on the routine
            // whose code has to honour it.
            var missing = into is not null ? broken & ~kept : Registers.None;

            // When the stack is unknown, that is why the restore could not be seen, and saying
            // what made it unknown points nearer the mistake than telling the routine to
            // restore the register again.
            var fix = missing != Registers.None
                ? Handing(into!, missing)
                : state.Stack is null && state.WhyStack is { } lost
                    ? Cause.Because(lost)
                    : $": restore {(one ? "it" : "them")} before returning, or add `.state keeps {items}` "
                        + "at the point where the entry value is back";
            report.Add(new Diagnostic(at,
                Catalogue.KeepsBroken.Says(
                    region.Routine.DisplayName, items, names, one ? "is" : "are", fix)));
        }

        /// <summary>
        /// What to write where handing control to another routine is what loses the registers:
        /// the promise goes on the routine handed to, since that is the code the register has
        /// to come back through, and control never comes back here to restore anything.
        /// </summary>
        private static string Handing(Symbol into, Registers missing)
        {
            var owner = Owner(into)!;
            var name = owner.DisplayName;
            var items = RegisterEffects.Spell(missing).ToLowerInvariant();
            var one = RegisterEffects.Each(missing).Count() == 1;

            // Where the path names a label rather than the routine itself, both names are worth
            // writing: one says where control went, the other where the promise belongs.
            var gone = owner == into
                ? $"control does not come back from `{name}`, which does not promise to keep {items}"
                : $"control does not come back from `{into.DisplayName}`, and `{name}` does not promise "
                    + $"to keep {items}";
            return $": {gone}: add `keeps {items}` to `{name}` if it preserves {(one ? "it" : "them")}, "
                + "or write `.next ?` here to end the path unchecked";
        }

        /// <summary>What one block does to the registers, from the state that reaches it.</summary>
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

        /// <summary>What one statement does to the registers.</summary>
        private RegisterState Step(Step step, RegisterState state, List<Diagnostic>? report)
        {
            if (step.Statement is StateDirectiveSyntax)
                return Asserted(step, state, report);
            if (step.Statement is not InstructionStatementSyntax statement)
                return state;

            var mnemonic = statement.Mnemonic.Text.ToLowerInvariant();
            var mode = layout.Of(statement, step.On)?.Mode;
            var facts = Instructions.Facts(mnemonic);

            // A software interrupt runs a handler that may not even be in this program.
            if (mnemonic is "brk" or "cop")
                return state.WithEach(Registers.All, RegisterValue.Unknown);

            // A call is handled for the block as a whole, because its effect depends on which
            // routine it reaches.
            if (facts.Control == Control.Calls)
                return state;

            // The processor pushes the flags when it takes an interrupt, and `rti` pulls them
            // back, so a handler that has left the stack where it found it hands the carry back
            // however it used it on the way.
            if (mnemonic == "rti")
                return state.With(Registers.C, state.Stack is { Depth: 0 } ? RegisterValue.Of(Registers.C) : RegisterValue.Unknown);

            if (facts.Pushes is { } push)
                return Saved(step, state, facts, push);
            if (facts.Pulls is { } pull)
                return Restored(step, state, facts, pull);

            // Moving the stack pointer leaves nothing known about the saves on the stack.
            if (mnemonic is "txs" or "tcs")
                state = state with { Stack = null };

            if (RegisterEffects.Moved(mnemonic) is { } moved)
                return state.With(moved.To, state.Of(moved.From));
            return state.WithEach(
                RegisterEffects.Written(mnemonic, mode, mode == AddressingMode.Immediate ? Constant(step) : null),
                RegisterValue.Written);
        }

        /// <summary>What a <c>.state keeps</c> says: from here, those registers hold what the routine was entered with.</summary>
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
                            Catalogue.KeepsRedundant.Says(item.Text, RegisterEffects.Spell(register))));
                    }
                    state = state.With(register, RegisterValue.Of(register));
                }
            }
            return state;
        }

        /// <summary>
        /// The state after a path passes control to a routine instead of returning: the
        /// registers that routine keeps are unchanged, and nothing is known of the rest.
        /// </summary>
        private static RegisterState Handed(RegisterState state, RoutineRegisters kept) =>
            state.WithEach(Registers.All & ~kept.Kept, RegisterValue.Unknown);

        /// <summary>What the routines a block calls leave behind.</summary>
        private static RegisterState Calls(BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of)
        {
            if (block.CallsUnknown || block.Calls.Count == 0)
                return state.WithEach(Registers.All, RegisterValue.Unknown);

            // A call through a pointer whose `.next` names several routines comes back with
            // whatever every one of them may have left.
            RegisterState? reached = null;
            foreach (var callee in block.Calls)
            {
                var kept = of(callee).Kept;
                reached = RegisterState.Merge(reached, state.WithEach(Registers.All & ~kept, RegisterValue.Unknown));
            }
            return reached!;
        }

        /// <summary>A push: what the register held goes on the stack, and nothing known for the rest.</summary>
        private RegisterState Saved(Step step, RegisterState state, InstructionFacts facts, PushSize size)
        {
            var value = facts.Held == Registers.None ? RegisterValue.Unknown : state.Of(facts.Held);
            return state with { Stack = state.Stack?.Push(new SavedPush(value, size, Width(step, size))) };
        }

        /// <summary>A pull: the register it fills gets back what the push it matches held.</summary>
        private RegisterState Restored(Step step, RegisterState state, InstructionFacts facts, PushSize size)
        {
            var width = Width(step, size);
            var value = state.Stack?.Pulled(size, width) ?? RegisterValue.Unknown;
            var pulled = state with { Stack = state.Stack?.Pull(size, width) };
            return facts.Held == Registers.None ? pulled : pulled.With(facts.Held, value);
        }

        /// <summary>
        /// How wide the register a push of this size moves is. Only the 65816 has widths, and a
        /// routine that changes neither width reads <see cref="Semantics.Width.Unchanged"/> at
        /// both the save and the restore, which is what lets the two cancel.
        /// </summary>
        private Semantics.Width Width(Step step, PushSize size)
        {
            if (states is null || size is PushSize.OneByte or PushSize.TwoBytes)
                return Semantics.Width.Eight;
            if (states.Before(step.Statement, step.On)?.Processor is not { } processor)
                return Semantics.Width.Unknown;
            return size == PushSize.Accumulator ? processor.A : processor.Index;
        }

        /// <summary>How a block ends: calling, jumping away for good, or returning.</summary>
        private (bool Calls, bool Tail, bool Returns) Ends(BasicBlock block)
        {
            if (block.Steps.Count == 0)
                return (false, false, false);
            var step = block.Steps[^1];
            var statement = step.Statement;
            var mode = layout.Of(statement, step.On)?.Mode;
            var transfer = Transfers.Of(statement, mode);
            var control = statement is InstructionStatementSyntax instruction
                ? Instructions.Facts(instruction.Mnemonic.Text).Control
                : Control.Through;
            var calls = transfer == Transfer.Call
                || flow.RelativeCallAt(step) is not null
                || (transfer == Transfer.Elsewhere && control == Control.Calls);

            // `stp` and `jam` stop the processor, so nothing ever reads what they left; `rti`
            // goes back to whatever the interrupt broke into, which is exactly where the
            // registers matter.
            var returns = transfer == Transfer.Return && block.Next is null && control != Control.Stops;
            var tail = !calls && !returns && (block.Calls.Count > 0 || block.CallsUnknown);
            return (calls, tail, returns);
        }

        /// <summary>
        /// What a block hands control to: the other routines, and the labels inside them, that
        /// its jump or its branch names, that a <c>.next</c> on it names in their place, or that
        /// the <c>.fallthrough</c> ending it runs into.
        /// Control never comes back from one, because that routine returns to this routine's
        /// caller, so the path ends there as a tail call's does.
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

        /// <summary>
        /// Whether a target hands control to a routine other than <paramref name="routine"/>.
        /// Another instance of the same family does not count as another routine: all the
        /// instances share one written body.
        /// </summary>
        private static bool Outside(Symbol target, Symbol routine) =>
            Owner(target) is { } owner && owner != routine && !owner.IsSiblingOf(routine);

        /// <summary>
        /// The routine a target hands control to: the routine itself where it names one, the
        /// routine a label is written inside where it names a label, and null where it names
        /// neither, which is a target this analysis has nothing to say about.
        /// </summary>
        private static Symbol? Owner(Symbol target) =>
            target.Signature is not null ? target
                : target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner
                : null;

        /// <summary>The value of an immediate operand, where it is known.</summary>
        private long? Constant(Step step) =>
            (step.Statement as InstructionStatementSyntax)?.Operand?.ChildNodes
                .OfType<ExpressionSyntax>().FirstOrDefault() is { } expression
                ? model.ValueOf(expression, step.On).AsNumber()
                : null;
    }
}
