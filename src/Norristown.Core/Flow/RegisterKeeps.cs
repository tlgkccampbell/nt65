using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;

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
/// <para>
/// This type runs the rounds over the whole program. <see cref="KeepsAnalysis"/>,
/// <see cref="ReadsAnalysis"/>, <see cref="StackPulls"/> and <see cref="ScopeKeeps"/> each answer
/// one question about one routine, over the walk <see cref="RegisterWalk"/> makes through it.
/// </para>
/// </summary>
public static class RegisterKeeps
{
    /// <summary>
    /// Works out what every routine of <paramref name="files"/> keeps and reads, stores both on
    /// each region, and reports the routines that break what they promise.
    /// </summary>
    /// <returns>
    /// The diagnostics, the routines that depend on the depth of the stack they were entered
    /// with, which what a routine reads is worked out from, and what each routine leaves on its
    /// caller's stack.
    /// </returns>
    internal static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlySet<RoutineKey> CallerStackReaders, StackEffects Effects) Compose(
        IReadOnlyList<FileAnalysis> files)
    {
        var regions = new Dictionary<RoutineKey, FlowRegion>();
        var walks = new Dictionary<RoutineKey, RegisterWalk>();
        var byFile = new List<(ControlFlow Flow, RegisterWalk Walk)>();
        foreach (var file in files)
        {
            var walk = new RegisterWalk(file.Model, file.Layout, file.Flow, file.State);
            byFile.Add((file.Flow, walk));
            foreach (var region in file.Flow.Regions)
            {
                var name = RoutineKey.Of(region.Routine);
                if (regions.TryAdd(name, region))
                    walks[name] = walk;
            }
        }

        var labels = Labels(regions, walks);

        // What a routine leaves on its caller's stack depends only on pushes and pulls, not on
        // what the registers hold, so it is worked out first, and every walk below applies it
        // after each call.
        var effects = StackEffects.Solve(regions, walks, labels);

        // Every routine starts out keeping everything, and each round takes away what the round
        // before it found. Nothing is ever added back, so the rounds stop. A label that another
        // routine calls or jumps to is an entry point of its own, and what it keeps is worked out
        // from there, alongside the routines. It keeps what its own path keeps, not what its
        // routine promises, because the promise is checked only from the routine's entry.
        //
        // An entry point is worked out again only when an answer it read last time has changed,
        // so after the first round only the callers of what moved are walked again.
        List<Entry> entries = [.. regions.Keys.Select(Entry.Routine), .. labels.Keys.Select(Entry.Label)];
        var found = regions.Keys.ToDictionary(name => name, _ => RoutineRegisters.Everything);
        var foundAt = labels.Keys.ToDictionary(name => name, _ => RoutineRegisters.Everything);
        var keepsReaders = new Dictionary<Entry, HashSet<Entry>>();
        Entry? keeping = null;
        Converge(entries, keepsReaders, entry =>
        {
            keeping = entry;
            if (!entry.IsLabel)
                return Narrow(found, entry.Key, Declared(regions[entry.Key].Routine, KeepsAnalysis.Of(walks[entry.Key], regions[entry.Key], Of, null)));
            var (owner, start) = labels[entry.Key];
            return Narrow(foundAt, entry.Key, KeepsAnalysis.Of(walks[owner], regions[owner], Of, null, start));
        });
        keeping = null;

        // With what every routine keeps settled, each routine is walked once more to check it and
        // to record what the registers hold along the way. Only that file's walk records anything,
        // so the files are walked at once, and what they report is gathered in the program's order.
        var diagnostics = new List<Diagnostic>(effects.Diagnostics);
        List<List<(RoutineKey Name, FlowRegion Region)>> inFiles =
            [.. regions.GroupBy(pair => walks[pair.Key]).Select(file => file.Select(pair => (pair.Key, pair.Value)).ToList())];
        var reported = new List<Diagnostic>[inFiles.Count];
        ParallelWork.For(inFiles.Count, i =>
        {
            reported[i] = [];
            foreach (var (name, region) in inFiles[i])
            {
                region.Registers = found[name];
                region.ScopeRegisters = ScopeKeeps.Of(walks[name], region, Of);
                KeepsAnalysis.Of(walks[name], region, Of, reported[i]);
            }
        }, CancellationToken.None);
        foreach (var file in reported)
            diagnostics.AddRange(file);
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
            entry => (entry.Value.Owner, StackPulls.Below(walks[entry.Value.Owner], regions[entry.Value.Owner], Of, entry.Value.Start)));
        var readers = CallerStack.Readers(files, pulls);
        var reads = regions.Keys.ToDictionary(name => name, _ => RoutineReads.Nothing);
        var readsAt = labels.Keys.ToDictionary(name => name, _ => RoutineReads.Nothing);
        var readsReaders = new Dictionary<Entry, HashSet<Entry>>();
        Entry? reading = null;
        Converge(entries, readsReaders, entry =>
        {
            reading = entry;
            if (!entry.IsLabel)
                return Widen(reads, entry.Key, ReadsAnalysis.Of(walks[entry.Key], regions[entry.Key], Of, ReadsOf, readers));
            var (owner, start) = labels[entry.Key];
            return Widen(readsAt, entry.Key, ReadsAnalysis.Of(walks[owner], regions[owner], Of, ReadsOf, readers, start: start));
        });
        reading = null;

        // A declared `keeps` is a contract, and a call that relies on more than it promises is
        // reported once what every routine keeps and reads is settled.
        foreach (var (name, region) in regions)
            UnpromisedKeeps.Check(walks[name], region, Of, ReadsOf, readers, Declining, diagnostics);

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

        // An editor asks about one routine at a time, long after the rounds have finished, and
        // goes by the same answers they settled on.
        foreach (var (flow, _) in byFile)
        {
            flow.KeepsOf = Of;
            flow.ReadsOf = ReadsOf;
            flow.Effects = effects;
        }
        return (Norristown.Diagnostics.Ordered(diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message))), readers, effects);

        // Reports each register a routine reads that its `reads` does not list, at the first place
        // on each path where its entry value is used.
        void Undeclared(FlowRegion region, Registers declared, RegisterWalk walk, List<Diagnostic> report)
        {
            var sites = new Dictionary<Registers, (Step Step, Symbol? Through)>();
            ReadsAnalysis.Of(walk, region, Of, ReadsOf, readers, sites);
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
            {
                Read(keepsReaders, keeping, Entry.Label(RoutineKey.Of(target)));
                return there;
            }
            if (found.ContainsKey(RoutineKey.Of(routine)))
                Read(keepsReaders, keeping, Entry.Routine(RoutineKey.Of(routine)));
            return found.TryGetValue(RoutineKey.Of(routine), out var known) ? known
                : routine.Signature?.Keeps is { } keeps && keeps != Registers.None ? new RoutineRegisters(keeps, true, keeps)
                : RoutineRegisters.Nothing;
        }

        // Returns the routine whose `keeps` leaves out a register that a call to target relies
        // on, following the calls of routines that declare no `keeps` down to one that does.
        Symbol Declining(Symbol target, Registers register)
        {
            var seen = new HashSet<Symbol>();
            var routine = target is { Kind: SymbolKind.Label, Routine: { } owner } ? owner : target;
            while (routine.Signature?.Keeps is null or Registers.None
                && regions.TryGetValue(RoutineKey.Of(routine), out var region) && seen.Add(routine)
                && region.Blocks.SelectMany(block => block.Calls)
                    .FirstOrDefault(callee => (Of(callee).Unbacked & register) != Registers.None) is { } next)
            {
                routine = next is { Kind: SymbolKind.Label, Routine: { } inside } ? inside : next;
            }
            return routine;
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
            {
                if (!readsAt.TryGetValue(RoutineKey.Of(target), out var there))
                    return RoutineReads.Unknown;
                Read(readsReaders, reading, Entry.Label(RoutineKey.Of(target)));
                return there;
            }
            if (routine.Signature?.Reads is null && reads.ContainsKey(RoutineKey.Of(routine)))
                Read(readsReaders, reading, Entry.Routine(RoutineKey.Of(routine)));
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
        Dictionary<RoutineKey, FlowRegion> regions, Dictionary<RoutineKey, RegisterWalk> walks)
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
    /// Works out every entry point's answer with <paramref name="evaluate"/>, which returns
    /// whether the answer changed. Each entry point is worked out once, and then again whenever an
    /// answer that <paramref name="readers"/> says it read changes, until none changes.
    /// </summary>
    private static void Converge(
        List<Entry> entries, Dictionary<Entry, HashSet<Entry>> readers, Func<Entry, bool> evaluate)
    {
        var pending = new Queue<Entry>(entries);
        var queued = new HashSet<Entry>(entries);
        while (pending.TryDequeue(out var entry))
        {
            queued.Remove(entry);
            if (!evaluate(entry) || !readers.TryGetValue(entry, out var waiting))
                continue;
            foreach (var next in waiting)
            {
                if (queued.Add(next))
                    pending.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// Records in <paramref name="readers"/> that the entry point being worked out, where there is
    /// one, read the answer of <paramref name="answer"/>, so that it is worked out again when that
    /// answer changes.
    /// </summary>
    private static void Read(Dictionary<Entry, HashSet<Entry>> readers, Entry? evaluating, Entry answer)
    {
        if (evaluating is not { } reader)
            return;
        if (!readers.TryGetValue(answer, out var waiting))
            readers[answer] = waiting = [];
        waiting.Add(reader);
    }

    /// <summary>
    /// Narrows what <paramref name="name"/> is known to keep to what a round found, and returns
    /// whether that changed it.
    /// </summary>
    private static bool Narrow(Dictionary<RoutineKey, RoutineRegisters> found, RoutineKey name, RoutineRegisters computed)
    {
        var narrowed = new RoutineRegisters(
            found[name].Kept & computed.Kept, found[name].Complete && computed.Complete, found[name].Backed & computed.Backed);
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

    /// <summary>
    /// Returns what a routine keeps, with what it declares taken as kept too. What it declares is
    /// all it promises, so that is what it backs.
    /// </summary>
    private static RoutineRegisters Declared(Symbol routine, RoutineRegisters found) =>
        routine.Signature?.Keeps is { } keeps && keeps != Registers.None
            ? found with { Kept = found.Kept | keeps, Backed = keeps }
            : found;

    /// <summary>
    /// Represents an entry point whose answers are worked out, which is a routine or a label that
    /// another routine calls or jumps to.
    /// </summary>
    /// <param name="Key">The routine or the label.</param>
    /// <param name="IsLabel">Whether the entry point is a label.</param>
    private readonly record struct Entry(RoutineKey Key, bool IsLabel)
    {
        /// <summary>Returns the entry point at the start of a routine.</summary>
        public static Entry Routine(RoutineKey key) => new(key, false);

        /// <summary>Returns the entry point at a label.</summary>
        public static Entry Label(RoutineKey key) => new(key, true);
    }
}
