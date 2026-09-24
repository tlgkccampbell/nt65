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
    /// Works out what every routine of <paramref name="files"/> keeps and reads, stores both on
    /// each region, and reports the routines that break what they promise.
    /// </summary>
    /// <returns>
    /// The diagnostics, and the routines that depend on the depth of the stack they were entered
    /// with, which what a routine reads is worked out from.
    /// </returns>
    internal static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlySet<RoutineKey> CallerStackReaders) Compose(
        IReadOnlyList<FileAnalysis> files)
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

        var labels = Labels(regions, walks);

        // Every routine starts out keeping everything, and each round takes away what the round
        // before it found. Nothing is ever added back, so the rounds stop. A label that another
        // routine calls or jumps to is an entry point of its own, and what it keeps is worked out
        // from there, alongside the routines. It keeps what its own path keeps, not what its
        // routine promises, because the promise is checked only from the routine's entry.
        var found = regions.Keys.ToDictionary(name => name, _ => RoutineRegisters.Everything);
        var foundAt = labels.Keys.ToDictionary(name => name, _ => RoutineRegisters.Everything);
        bool moved;
        do
        {
            moved = false;
            foreach (var (name, region) in regions)
                moved |= Narrow(found, name, Declared(region.Routine, walks[name].Run(region, Of, null)));
            foreach (var (name, (owner, start)) in labels)
                moved |= Narrow(foundAt, name, walks[owner].Run(regions[owner], Of, null, start));
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

        // What a routine reads is worked out once what every routine keeps is settled, because a
        // call passes on the entry values of the registers it keeps to whatever uses them next.
        // Every routine starts out reading nothing, and each round adds what the round before it
        // found. Nothing is taken away, so the rounds stop.
        //
        // Entered at a label, a routine may pull what its path from the top pushed, which is then
        // what its caller pushed. So which labels reach below their entry on the stack is found
        // from each label, before which routines depend on the stack's depth is worked out.
        var pulls = labels.ToDictionary(
            entry => entry.Key,
            entry => (entry.Value.Owner, walks[entry.Value.Owner].PullsBelow(regions[entry.Value.Owner], Of, entry.Value.Start)));
        var readers = CallerStack.Readers(files, pulls);
        var reads = regions.Keys.ToDictionary(name => name, _ => RoutineReads.Nothing);
        var readsAt = labels.Keys.ToDictionary(name => name, _ => RoutineReads.Nothing);
        do
        {
            moved = false;
            foreach (var (name, region) in regions)
                moved |= Widen(reads, name, walks[name].Reads(region, Of, ReadsOf, readers));
            foreach (var (name, (owner, start)) in labels)
                moved |= Widen(readsAt, name, walks[owner].Reads(regions[owner], Of, ReadsOf, readers, start: start));
        }
        while (moved);

        // A routine that declares what it reads shows its declaration, which is what its callers
        // go by, and is checked against what its body was found to read.
        foreach (var (name, region) in regions)
        {
            if (region.Routine.Signature?.Reads is not { } declared)
            {
                region.Reads = reads[name];
                continue;
            }
            region.Reads = new RoutineReads(declared, true);
            if ((reads[name].Read & ~declared) != Registers.None)
                Undeclared(region, declared, walks[name], diagnostics);
        }
        return (Norristown.Diagnostics.Ordered(diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message))), readers);

        // Reports each register a routine reads that its `reads` does not list, at the first place
        // on each path where its entry value is used.
        void Undeclared(FlowRegion region, Registers declared, Walk walk, List<Diagnostic> report)
        {
            var sites = new Dictionary<Registers, (Step Step, Symbol? Through)>();
            walk.Reads(region, Of, ReadsOf, readers, sites);
            var routine = region.Routine;
            // The item is the signature's own where the signature writes one, and otherwise comes
            // from a signature set, which a fix here should not change.
            var item = StateItem.Read(routine.Signature!.Syntax).FirstOrDefault(item => item.Part == StatePart.Reads);
            var written = item.Node is null ? $"reads {Listed(declared)}" : item.Text;
            foreach (var (register, (step, through)) in sites)
            {
                if ((declared & register) != Registers.None)
                    continue;
                var name = RegisterEffects.Format(register).ToLowerInvariant();
                var via = through is null ? "" : $" through `{through.DisplayName}`";
                var fix = register == Registers.C
                    ? $": add `{name}` to `reads`, or set the carry with `clc` or `sec` before it is used"
                    : $": add `{name}` to `reads`, or give {RegisterEffects.Format(register)} a value before it is used";
                report.Add(new Diagnostic(
                    step.Statement.Tree.GetSpan(step.Statement.Span),
                    Catalogue.ReadsUndeclared.Message(routine.DisplayName, written, RegisterEffects.Format(register), via + fix))
                {
                    Fix = item.Node is not null ? new DiagnosticFix(FixKind.Reads, name, routine.DeclarationSpan) : null,
                });
            }
        }

        // Returns what a routine keeps. That is what the walk found or, for a routine whose body
        // is not in the program, what it declares. A routine that declares more than its body
        // shows is taken at its word, so the mistake is reported at the declaration and not at
        // every call.
        //
        // A label another routine calls or jumps to keeps what the path from that label keeps. A
        // label no other routine reaches is treated as the routine it is inside.
        //
        // A routine that never returns is treated as keeping every register, because no caller
        // ever sees what it leaves in them. A path that calls it or jumps into it ends there.
        RoutineRegisters Of(Symbol target)
        {
            var routine = target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner : target;
            if (routine.Signature is { NeverReturns: true })
                return RoutineRegisters.Everything;
            if (routine != target && foundAt.TryGetValue(RoutineKey.Of(target), out var there))
                return there;
            return found.TryGetValue(RoutineKey.Of(routine), out var known) ? known
                : routine.Signature?.Keeps is { } keeps && keeps != Registers.None ? new RoutineRegisters(keeps, true)
                : RoutineRegisters.Nothing;
        }

        // Returns what a routine reads. A routine that declares it is taken at its word, as its
        // callers are, and one whose body breaks the declaration is reported there. A routine
        // whose body is not in the program and that declares nothing may read anything.
        //
        // A label another routine calls or jumps to reads what the path from that label reads.
        // Any other label may read anything, because the entry values its routine reads may be
        // ones its own code wrote before the label.
        RoutineReads ReadsOf(Symbol target)
        {
            var routine = target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner : target;
            if (routine != target)
                return readsAt.TryGetValue(RoutineKey.Of(target), out var there) ? there : RoutineReads.Unknown;
            return routine.Signature?.Reads is { } declared ? new RoutineReads(declared, true)
                : reads.TryGetValue(RoutineKey.Of(routine), out var known) ? known
                : RoutineReads.Unknown;
        }
    }

    /// <summary>
    /// Returns every label that a routine calls, jumps to or branches to inside another routine, or
    /// inside itself by a call, with the routine it is in and the index of the block it starts.
    /// Each is an entry point of its own, entered as a call enters a routine.
    /// </summary>
    private static Dictionary<RoutineKey, (RoutineKey Owner, int Start)> Labels(
        Dictionary<RoutineKey, FlowRegion> regions, Dictionary<RoutineKey, Walk> walks)
    {
        var starts = new Dictionary<RoutineKey, (RoutineKey Owner, int Start)>();
        foreach (var (name, region) in regions)
        {
            foreach (var block in region.Blocks)
            {
                if (block.Label is { Kind: SymbolKind.Label } label)
                    starts.TryAdd(RoutineKey.Of(label), (name, block.Index));
            }
        }

        var labels = new Dictionary<RoutineKey, (RoutineKey Owner, int Start)>();
        foreach (var (name, region) in regions)
        {
            foreach (var block in region.Blocks)
            {
                if (!block.IsReached)
                    continue;
                foreach (var target in block.Calls.Concat(walks[name].Leaves(block, region.Routine)))
                {
                    if (target is { Kind: SymbolKind.Label, Routine: not null }
                        && starts.TryGetValue(RoutineKey.Of(target), out var start))
                    {
                        labels.TryAdd(RoutineKey.Of(target), start);
                    }
                }
            }
        }
        return labels;
    }

    /// <summary>
    /// Narrows what <paramref name="name"/> is known to keep to what a round found, and returns
    /// whether that changed it.
    /// </summary>
    private static bool Narrow(Dictionary<RoutineKey, RoutineRegisters> found, RoutineKey name, RoutineRegisters computed)
    {
        var narrowed = new RoutineRegisters(found[name].Kept & computed.Kept, found[name].Complete && computed.Complete);
        if (narrowed == found[name])
            return false;
        found[name] = narrowed;
        return true;
    }

    /// <summary>
    /// Widens what <paramref name="name"/> is known to read to what a round found, and returns
    /// whether that changed it.
    /// </summary>
    private static bool Widen(Dictionary<RoutineKey, RoutineReads> found, RoutineKey name, RoutineReads computed)
    {
        var widened = new RoutineReads(found[name].Read | computed.Read, found[name].Complete && computed.Complete);
        if (widened == found[name])
            return false;
        found[name] = widened;
        return true;
    }

    /// <summary>Formats registers as a <c>reads</c> item lists them, with <c>none</c> for no register.</summary>
    private static string Listed(Registers registers) =>
        registers == Registers.None ? "none" : RegisterEffects.Format(registers).ToLowerInvariant();

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

        // What reaches each block of a routine from each place it is entered, kept from the first
        // time what the routine reads is asked. It depends only on what every routine keeps,
        // which is settled by then.
        private readonly Dictionary<(FlowRegion Region, int Start), RegisterState?[]> solved = [];

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
        /// pass null and report nothing. <paramref name="start"/> is the index of the block the
        /// routine is entered at, which is a label's where another routine calls or jumps to it.
        /// </summary>
        public RoutineRegisters Run(
            FlowRegion region, Func<Symbol, RoutineRegisters> of, List<Diagnostic>? report, int start = 0)
        {
            var blocks = region.Blocks;
            if (!region.IsEntered || blocks.Count == 0)
                return RoutineRegisters.Everything;

            var reached = Solve(region, of, start);
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
        /// Returns which registers <paramref name="region"/>'s routine uses the entry values of.
        /// <paramref name="of"/> gives what each routine it passes control to keeps, which must be
        /// settled, and <paramref name="reads"/> what each reads. <paramref name="readers"/> are the
        /// routines that reach below their own entry on the stack, which use what their caller
        /// pushed. <paramref name="sites"/>, where it is given, collects the first statement found
        /// to use each register's entry value, and the routine it was used through, if any.
        /// </summary>
        public RoutineReads Reads(
            FlowRegion region, Func<Symbol, RoutineRegisters> of, Func<Symbol, RoutineReads> reads,
            IReadOnlySet<RoutineKey> readers, Dictionary<Registers, (Step Step, Symbol? Through)>? sites = null,
            int start = 0)
        {
            var blocks = region.Blocks;
            if (!region.IsEntered || blocks.Count == 0)
                return RoutineReads.Nothing;
            if (!solved.TryGetValue((region, start), out var reached))
                solved[(region, start)] = reached = Solve(region, of, start);

            var read = Registers.None;
            var complete = true;
            var left = new RegisterState?[blocks.Count];
            foreach (var block in blocks)
            {
                if (reached[block.Index] is not { } state)
                    continue;
                var ends = Ends(block);
                for (var i = 0; i < block.Steps.Count; i++)
                {
                    var step = block.Steps[i];
                    var saved = i + 1 < block.Steps.Count ? Saved(step, block.Steps[i + 1], state) : Registers.None;
                    state = Step(step, state, null, entries => Use(entries, step, null), saved);
                    if (i == block.Steps.Count - 1 && (ends.Calls || ends.Tail))
                    {
                        Called(block, state, ends.Tail);
                        state = Calls(block, state, of);
                    }
                }

                // Returning with something still pushed returns through it, and a routine this one
                // passes control to may pull it.
                foreach (var into in Leaves(block, region.Routine))
                {
                    Given(reads(into), state, block, into);
                    Use(state.Stack?.Entries ?? Registers.None, block.Steps[^1], into);
                }
                if (ends.Returns)
                    Use(state.Stack?.Entries ?? Registers.None, block.Steps[^1], null);
                left[block.Index] = state;
            }

            // Where two paths that pushed different things meet, nothing is known about the stack
            // after it, and a later pull may take any of those pushes back.
            foreach (var block in blocks)
            {
                if (left[block.Index] is not { Stack: { } stack })
                    continue;
                if (flow.Onward(blocks, block).Any(to => reached[to] is { Stack: null }))
                    Use(stack.Entries, block.Steps[^1], null);
            }
            return new RoutineReads(read, complete);

            void Use(Registers entries, Step at, Symbol? through)
            {
                read |= entries;
                if (sites is null)
                    return;
                foreach (var register in RegisterEffects.Each(entries))
                    sites.TryAdd(register, (at, through));
            }

            // Adds what a routine uses of the values the registers hold where control passes to it.
            // One that may read anything leaves the answer incomplete only where something there
            // still holds one of this routine's entry values.
            void Given(RoutineReads callee, RegisterState state, BasicBlock block, Symbol through)
            {
                if (!callee.Complete && Holds(state))
                    complete = false;
                foreach (var register in RegisterEffects.Each(callee.Read))
                    Use(state.Whole(register).Entry, block.Steps[^1], through);
            }

            // Adds what the routines a block ends by calling use. A call nt65 cannot follow may use
            // anything, which only an incomplete answer can say, as for a routine whose body is
            // not in the program. A routine that takes arguments, or that reaches below its own
            // entry on the stack, uses what is pushed, and so may the routine a tail call hands the
            // stack to.
            void Called(BasicBlock block, RegisterState state, bool tail)
            {
                var pushed = state.Stack?.Entries ?? Registers.None;
                if (block.CallsUnknown || block.Calls.Count == 0)
                {
                    if (Holds(state))
                        complete = false;
                    return;
                }
                foreach (var callee in block.Calls)
                {
                    Given(reads(callee), state, block, callee);
                    if (tail || callee.Signature is { Arguments: > 0 } || readers.Contains(RoutineKey.Of(callee)))
                        Use(pushed, block.Steps[^1], callee);
                }
            }
        }

        /// <summary>
        /// Returns whether anything code nt65 cannot follow could read at a point may hold one of
        /// the routine's entry values. Such code sees only the registers and the stack, so where
        /// every register holds something else and nothing pushed holds an entry value, it cannot
        /// read any of them. A stack whose contents are not known may hold anything.
        /// </summary>
        private static bool Holds(RegisterState state) =>
            state.Stack is not { } stack || stack.Entries != Registers.None
            || RegisterEffects.Each(Registers.All).Any(register => state.Whole(register).Entry != Registers.None);

        /// <summary>
        /// Returns whether <paramref name="region"/>'s routine, entered at the block at
        /// <paramref name="start"/>, may pull more than it has pushed on the way, which takes what
        /// its caller pushed. A pull where what is on the stack is not known counts.
        /// </summary>
        public bool PullsBelow(FlowRegion region, Func<Symbol, RoutineRegisters> of, int start)
        {
            var reached = Solve(region, of, start);
            foreach (var block in region.Blocks)
            {
                if (reached[block.Index] is not { } state)
                    continue;
                foreach (var step in block.Steps)
                {
                    if (step.Statement is InstructionStatementSyntax statement
                        && Instructions.Facts(statement.MnemonicKind).Pulls is not null
                        && state.Stack is not { Depth: > 0 })
                    {
                        return true;
                    }
                    state = Step(step, state, null);
                }
            }
            return false;
        }

        /// <summary>
        /// Returns what reaches each block of <paramref name="region"/>, where the routine is entered
        /// at the block at <paramref name="start"/>, with <paramref name="of"/> giving what each
        /// routine it calls keeps.
        /// </summary>
        public RegisterState?[] Solve(FlowRegion region, Func<Symbol, RoutineRegisters> of, int start)
        {
            var blocks = region.Blocks;
            var solver = Solver(blocks, of, block => flow.Onward(blocks, block));
            solver.Enter(start, RegisterState.Entered);

            // A label a `.state` declares may be jumped into from another routine, so the
            // registers there hold nothing this routine put in them. The stack there is what a
            // call to the routine leaves, which is empty. Code that jumps in has made none of
            // this routine's saves, so a save that the path above the label leaves on the stack
            // cannot be shown to be the one a pull below the label takes back. Entered at a label,
            // the routine's other entry points are not part of the answer unless the path from
            // that label reaches them.
            solver.EnterDeclared(
                outside, start == 0 ? RegisterState.Outside : null,
                (block, state) => Entered(state, block, region.Routine));
            return solver.Reached;
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
        /// Where control goes to a label inside a routine, the path from that label has to restore
        /// the registers, since a promise on the routine does not cover it.
        /// </summary>
        private static string Handing(Symbol into, Registers missing)
        {
            var owner = Owner(into)!;
            var name = owner.DisplayName;
            var items = RegisterEffects.Format(missing).ToLowerInvariant();
            var one = RegisterEffects.Each(missing).Count() == 1;

            // Where the path names a label rather than the routine itself, what is kept is what the
            // path from that label keeps, which no promise on the routine changes.
            if (owner != into)
            {
                return $": control does not come back from `{into.DisplayName}` in `{name}`, and the path from "
                    + $"there does not keep {items}: restore {(one ? "it" : "them")} there, "
                    + "or add `.next ?` here to end the path unchecked";
            }
            var gone = $"control does not come back from `{name}`, which does not promise to keep {items}";
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
            var above = state;
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                if (report is not null && !step.Closes)
                    Held.Record(step, state);
                if (report is not null && step.Statement is StateDirectiveSyntax)
                    CheckSaves(i > 0 ? block.Steps[i - 1] : null, step, above, report);
                above = state;
                state = Step(step, state, report);
                if (i == block.Steps.Count - 1 && (ends.Calls || ends.Tail))
                    state = Calls(block, state, of);
            }
            return state;
        }

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
        /// Returns what one statement does to the registers. <paramref name="use"/>, where it is
        /// given, is told the entry values the statement uses.
        /// </summary>
        private RegisterState Step(
            Step step, RegisterState state, List<Diagnostic>? report, Action<Registers>? use = null,
            Registers saved = Registers.None)
        {
            if (step.Statement is StateDirectiveSyntax)
                return Asserted(step, state, report);
            if (step.Statement is not InstructionStatementSyntax statement)
                return state;

            var mnemonic = statement.MnemonicKind;
            var mode = layout.Of(statement, step.On)?.Mode;
            var facts = Instructions.Facts(mnemonic);
            if (use is not null)
                Used(step, mnemonic, mode, state, use, saved);

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
                return Restored(step, state, facts, pull, use);

            // Moving the stack pointer leaves nothing known about the saves on the stack.
            if (mnemonic is MnemonicKind.Txs or MnemonicKind.Tcs)
                state = state with { Stack = null };

            if (RegisterEffects.Moved(mnemonic) is { } moved)
            {
                return moved.To == Registers.A ? Accumulator(step, state, state.Of(moved.From))
                    : state.With(moved.To, moved.From == Registers.A ? Taken(step, mnemonic, state) : state.Of(moved.From));
            }
            var written = RegisterEffects.Written(
                mnemonic, mode, mode == AddressingMode.Immediate ? Constant(step) : null);
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
            var everything = mnemonic is MnemonicKind.Brk or MnemonicKind.Cop;
            var read = everything ? Registers.All : RegisterEffects.Read(mnemonic, mode) & ~saved;
            foreach (var register in RegisterEffects.Each(read))
            {
                use((everything ? state.Whole(register)
                    : register == Registers.A ? Taken(step, mnemonic, state)
                    : state.Of(register)).Entry);
            }

            // Reading the stack pointer, moving it, or addressing the stack by offset reaches the
            // pushes in some way other than pulling them back in order.
            if (read == Registers.All
                || mnemonic is MnemonicKind.Tsx or MnemonicKind.Tsc or MnemonicKind.Txs or MnemonicKind.Tcs
                || mode is AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
            {
                use(state.Stack?.Entries ?? Registers.None);
            }
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
        /// Returns whether the accumulator, or the index registers where <paramref name="index"/>
        /// is true, are 16 bits wide before a step, or null where that is not known. Every CPU but
        /// the 65816 has no high byte to speak of, and there the answer is true, so that a write
        /// covers the whole register.
        /// </summary>
        private bool? Wide(Step step, bool index)
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
            return state with { Stack = state.Stack?.Push(new SavedPush(value, size, Width(step, size))) };
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
            var pulled = state with { Stack = state.Stack?.Pull(size, width) };

            // A pull that does not match the push on top takes bytes of pushes other than the one
            // it restores, so what they hold is used.
            if (use is not null && state.Stack is { } stack && pulled.Stack is null)
                use(stack.Entries);
            return facts.Held switch
            {
                Registers.None => pulled,
                Registers.A => Accumulator(step, pulled, value),
                _ => pulled.With(facts.Held, value),
            };
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
        public IEnumerable<Symbol> Leaves(BasicBlock block, Symbol routine)
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
