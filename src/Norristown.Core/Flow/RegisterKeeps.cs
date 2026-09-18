using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Which registers each routine hands back as it was entered with them, worked out across the
/// program. A 6502 programmer's first question about someone else's routine is which registers
/// survive it, and it is the one question about a routine nt65 used to leave to a comment.
/// <para>
/// A routine is followed the way the 65816's state is: its blocks are run to a fixed point over
/// what each register may hold, and a save and its restore cancel through the stack, so
/// <c>pha</c> … <c>pla</c> around a call needs nothing written. What its calls do is worked out
/// with it, over the whole program: every routine starts out keeping everything, and what each
/// one keeps is taken away until nothing moves. A set that only shrinks is what makes two
/// routines that call each other settle rather than go round for ever, which is the one place
/// this is easier than the cycle counts.
/// </para>
/// <para>
/// A restore through memory is not seen, because ruling out every store that could have reached
/// the byte would need addresses, which are the linker's. A <c>.state keeps a</c> where the
/// value comes back says what the analysis cannot.
/// </para>
/// </summary>
public static class RegisterKeeps
{
    /// <summary>
    /// Works out what every routine of <paramref name="flows"/> keeps, writes it on each region,
    /// and reports the routines that break what they promise. The lists run in one order, and
    /// <paramref name="states"/> is empty on the CPUs that have no widths to follow.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Compose(
        IReadOnlyList<SemanticModel> models, IReadOnlyList<CodeLayout> layouts,
        IReadOnlyList<ControlFlow> flows, IReadOnlyList<StateAnalysis> states)
    {
        var regions = new Dictionary<(string Path, int At), FlowRegion>();
        var walks = new Dictionary<(string Path, int At), Walk>();
        for (var i = 0; i < flows.Count && i < models.Count && i < layouts.Count; i++)
        {
            var walk = new Walk(models[i], layouts[i], flows[i], i < states.Count ? states[i] : null);
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
            walks[name].Run(region, Of, diagnostics);
        }
        return Norristown.Diagnostics.Ordered(diagnostics.DistinctBy(d => (d.Span, d.Message)));

        // What a routine keeps: what the walk found, or, for one whose body is not here, what
        // it declares. A routine that declares more than its body shows is taken at its word,
        // so the mistake is reported where it is written and not at every call.
        RoutineRegisters Of(Symbol routine) =>
            found.TryGetValue(Named(routine), out var known) ? known
                : routine.Signature?.Keeps is { } keeps && keeps != Registers.None ? new RoutineRegisters(keeps, true)
                : RoutineRegisters.Nothing;
    }

    /// <summary>What a routine keeps, with what it declares taken as kept too.</summary>
    private static RoutineRegisters Declared(Symbol routine, RoutineRegisters found) =>
        routine.Signature?.Keeps is { } keeps && keeps != Registers.None
            ? found with { Kept = found.Kept | keeps }
            : found;

    /// <summary>
    /// A routine by where it is declared, rather than by the symbol, which an analysis that kept
    /// the file before an edit may hold a different object for.
    /// </summary>
    private static (string Path, int At) Named(Symbol routine) => (routine.Tree.Path, routine.NameSpan.Start);

    /// <summary>One file's routines, followed a routine at a time.</summary>
    private sealed class Walk
    {
        private readonly SemanticModel model;
        private readonly CodeLayout layout;
        private readonly ControlFlow flow;
        private readonly StateAnalysis? states;

        public Walk(SemanticModel model, CodeLayout layout, ControlFlow flow, StateAnalysis? states)
        {
            this.model = model;
            this.layout = layout;
            this.flow = flow;
            this.states = states;
        }

        /// <summary>
        /// What <paramref name="region"/>'s routine keeps, with <paramref name="of"/> saying what
        /// each routine it calls keeps. <paramref name="report"/> takes what is wrong with it, on
        /// the walk over the settled answer; the rounds before it report nothing.
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

            // A label a `.state` declares is somewhere another routine may jump into, so what
            // the registers hold there is nothing this routine put in them.
            foreach (var block in blocks)
            {
                if (!block.IsDeclared || reached[block.Index] is not null)
                    continue;
                reached[block.Index] = RegisterState.Unknown;
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
                if (!ends.Returns && !ends.Tail)
                    continue;
                leaves = true;
                kept &= after.Kept;
                if (report is not null)
                    Check(region, block, after, report);
            }

            // A routine no path leaves hands nothing back, so there is nothing it can fail to
            // keep; what it does to the registers is no one's business but its own.
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

            // The blocks the state after a block travels to, as the 65816's analysis reads
            // them: a call goes on at the statement after it, and a jump to a routine's entry
            // leaves this one.
            IEnumerable<int> Carried(BasicBlock block)
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
        }

        /// <summary>Whether a routine's promise holds where a path leaves it, and what to write when it does not.</summary>
        private void Check(FlowRegion region, BasicBlock block, RegisterState state, List<Diagnostic> report)
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
            report.Add(new Diagnostic(at, Severity.Error,
                $"`{region.Routine.DisplayName}` promises `keeps {items}`, and {names} {(one ? "is" : "are")} not what "
                + $"the routine was entered with here: restore {(one ? "it" : "them")} before returning, or a "
                + $"`.state keeps {items}` where the value comes back says so"));
        }

        /// <summary>What one block does to the registers, from the state that reaches it.</summary>
        private RegisterState Through(
            BasicBlock block, RegisterState state, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report)
        {
            var ends = Ends(block);
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                state = Step(step, state, report);
                if (i == block.Steps.Count - 1 && (ends.Calls || ends.Tail))
                    state = Calls(block, state, of);
            }
            return state;
        }

        /// <summary>What one statement does to the registers.</summary>
        private RegisterState Step(Step step, RegisterState state, List<Diagnostic>? report)
        {
            var statement = step.Statement;
            if (statement.Kind == SyntaxKind.StateDirective)
                return Asserted(step, state, report);
            if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
                return state;

            var mnemonic = statement.ChildTokens[0].Text.ToLowerInvariant();
            var mode = layout.Of(statement, step.On)?.Mode;

            // A software interrupt runs a handler that may not even be in this program.
            if (mnemonic is "brk" or "cop")
                return state.WithEach(Registers.All, RegisterValue.Unknown);

            // A call is the block's business, because what it does depends on what it reaches.
            if (mnemonic is "jsr" or "jsl")
                return state;

            // The processor pushes the flags when it takes an interrupt, and `rti` pulls them
            // back, so a handler that has left the stack where it found it hands the carry back
            // however it used it on the way.
            if (mnemonic == "rti")
                return state.With(Registers.C, state.Stack is { Depth: 0 } ? RegisterValue.Of(Registers.C) : RegisterValue.Unknown);

            if (Pushed(mnemonic) is { } push)
                return Saved(step, state, mnemonic, push);
            if (Pulled(mnemonic) is { } pull)
                return Restored(step, state, mnemonic, pull);

            // The stack pointer moving puts the saves somewhere nothing is known of.
            if (mnemonic is "txs" or "tcs")
                state = state with { Stack = null };

            if (RegisterEffects.Moved(mnemonic) is { } moved)
                return state.With(moved.To, state.Of(moved.From));
            return state.WithEach(
                RegisterEffects.Written(mnemonic, mode, mode == AddressingMode.Immediate ? Constant(step) : null),
                RegisterValue.Written);
        }

        /// <summary>What a <c>.state keeps</c> says: from here, those registers hold what the routine was entered with.</summary>
        private RegisterState Asserted(Step step, RegisterState state, List<Diagnostic>? report)
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
                            step.Statement.Tree.GetSpan(item.Node.Span), Severity.Warning,
                            $"`{item.Text}` says nothing here: {RegisterEffects.Spell(register)} is already "
                            + "what the routine was entered with"));
                    }
                    state = state.With(register, RegisterValue.Of(register));
                }
            }
            return state;
        }

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

        /// <summary>The push a mnemonic makes, and what it pushes, or null when it pushes nothing.</summary>
        private static (PushSize Size, Registers? Register)? Pushed(string mnemonic) => mnemonic switch
        {
            "pha" => (PushSize.Accumulator, Registers.A),
            "phx" => (PushSize.Index, Registers.X),
            "phy" => (PushSize.Index, Registers.Y),
            "php" => (PushSize.OneByte, Registers.C),
            "phb" or "phk" => (PushSize.OneByte, null),
            "phd" or "pea" or "pei" or "per" => (PushSize.TwoBytes, null),
            _ => null,
        };

        /// <summary>The pull a mnemonic makes, and where it puts it, or null when it pulls nothing.</summary>
        private static (PushSize Size, Registers? Register)? Pulled(string mnemonic) => mnemonic switch
        {
            "pla" => (PushSize.Accumulator, Registers.A),
            "plx" => (PushSize.Index, Registers.X),
            "ply" => (PushSize.Index, Registers.Y),
            "plp" => (PushSize.OneByte, Registers.C),
            "plb" => (PushSize.OneByte, null),
            "pld" => (PushSize.TwoBytes, null),
            _ => null,
        };

        private RegisterState Saved(Step step, RegisterState state, string mnemonic, (PushSize Size, Registers? Register) push)
        {
            var value = push.Register is { } register && mnemonic != "phb" && mnemonic != "phk"
                ? state.Of(register)
                : RegisterValue.Unknown;
            return state with { Stack = state.Stack?.Push(new SavedPush(value, push.Size, Width(step, push.Size))) };
        }

        private RegisterState Restored(Step step, RegisterState state, string mnemonic, (PushSize Size, Registers? Register) pull)
        {
            var width = Width(step, pull.Size);
            var value = state.Stack?.Pulled(pull.Size, width) ?? RegisterValue.Unknown;
            var pulled = state with { Stack = state.Stack?.Pull(pull.Size, width) };
            return pull.Register is { } register ? pulled.With(register, value) : pulled;
        }

        /// <summary>
        /// How wide the register a push of this size moves is. Only the 65816 has widths, and a
        /// routine that changes neither reads the same unchanged width at the save and the
        /// restore, which is what lets the two cancel.
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
            var mnemonic = statement.Kind == SyntaxKind.InstructionStatement && statement.ChildTokens.Length > 0
                ? statement.ChildTokens[0].Text.ToLowerInvariant()
                : "";
            var calls = transfer == Transfer.Call
                || flow.RelativeCallAt(step) is not null
                || (transfer == Transfer.Elsewhere && mnemonic is "jsr" or "jsl");

            // `stp` stops the processor, so nothing ever reads what it left; `rti` goes back to
            // whatever the interrupt broke into, which is exactly where the registers matter.
            var returns = transfer == Transfer.Return && block.Next is null && mnemonic != "stp";
            var tail = !calls && !returns && (block.Calls.Count > 0 || block.CallsUnknown);
            return (calls, tail, returns);
        }

        /// <summary>The value of an immediate operand, where it is known.</summary>
        private long? Constant(Step step) =>
            step.Statement.ChildNodes.FirstOrDefault()?.ChildNodes
                .FirstOrDefault(child => child.Kind != SyntaxKind.AddressPrefix) is { } expression
                ? model.ValueOf(expression, step.On).AsNumber()
                : null;
    }
}
