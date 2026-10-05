using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Reports reads of scratch that a call may have overwritten since it was stored, and scratch used
/// by code an interrupt handler runs. Scratch is data that <c>.scratch</c> declares. Routines share
/// its bytes for working values, and a caller may store an argument there for the routine it calls.
/// A call that may store to scratch leaves nothing there that the caller can rely on.
/// <para>
/// The checks follow only instructions that name scratch directly, never what memory holds. A
/// direct store to a byte of scratch makes it the routine's own again. A call to a routine that
/// may store to the byte, directly or through a routine it calls in turn, makes it doubtful. A
/// read of a doubtful byte is reported, and so is a call to a routine that reads a doubtful byte
/// before storing to it, which is an argument an earlier call overwrote. An indexed store counts as
/// a store to every byte of the scratch it names, and a call nt65 cannot follow is taken to store
/// nothing.
/// </para>
/// <para>
/// Scratch belongs to the code outside interrupt handlers. A handler can run between any two
/// instructions, so scratch it shared could change under the code it interrupted. A handler
/// therefore keeps its working storage in <c>.data</c>, and any use of scratch by a routine a
/// handler reaches is an error. The rule does not ask what kind of interrupt enters a handler,
/// because nothing can hold a handler to one kind.
/// </para>
/// </summary>
internal static class ScratchChecks
{
    /// <summary>
    /// Returns the warnings about scratch over every routine of <paramref name="analysis"/>,
    /// together with the scratch each routine reads and stores to. A program that declares no
    /// scratch is not walked.
    /// </summary>
    public static (IReadOnlyList<Diagnostic> Found, IReadOnlyDictionary<RoutineKey, ScratchUse> Uses) Check(
        ProgramAnalysis analysis)
    {
        var files = analysis.Files;
        if (!files.Any(file => file.Model.Symbols.Any(Location.IsScratch)))
            return ([], new Dictionary<RoutineKey, ScratchUse>());

        var program = new ProgramScratch(files, new MemoryInference(analysis));
        var found = new List<Diagnostic>();
        var reported = new HashSet<(Span, string)>();
        var uses = new Dictionary<RoutineKey, ScratchUse>();
        foreach (var routine in program.Routines.Values)
        {
            foreach (var diagnostic in Walk(routine, program))
            {
                if (reported.Add((diagnostic.Span, diagnostic.Message)))
                    found.Add(diagnostic);
            }
            uses[routine.Key] = new ScratchUse(
                Shown(program.Inference.ReadsOf(routine.Symbol).Select(Slot.Of)),
                Shown(routine.Stores.Keys.Select(slot => (Slot?)slot)));
        }
        found.AddRange(InHandlers(program));
        return (found, uses);
    }

    /// <summary>
    /// Returns the names of the scratch that <paramref name="slots"/> are bytes of, each once and
    /// in order.
    /// </summary>
    private static IReadOnlyList<string> Shown(IEnumerable<Slot?> slots) =>
        [.. slots.OfType<Slot>().Select(slot => slot.Shown).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Returns a warning for each read in <paramref name="routine"/> of scratch that a call may
    /// have overwritten since the routine last stored it. The routine is entered at its start and
    /// at each label a <c>.state</c> declares an entry point, with nothing doubtful.
    /// </summary>
    private static List<Diagnostic> Walk(Routine routine, ProgramScratch program)
    {
        var found = new List<Diagnostic>();
        var (blocks, file) = (routine.Region.Blocks, routine.File);
        if (blocks.Count == 0)
            return found;
        var solver = new Dataflow<Doubtful>(
            blocks, (block, doubtful) => Through(block, doubtful, null), Doubtful.Merge,
            block => ControlFlow.Onward(blocks, block));
        solver.Enter(0, Doubtful.Nothing);
        foreach (var block in blocks)
        {
            if (block.IsDeclared && solver.Reached[block.Index] is null)
                solver.Enter(block.Index, Doubtful.Nothing);
        }
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is { } doubtful)
                Through(block, doubtful, found);
        }
        return found;

        // Returns what is doubtful after the block, and reports each read of a doubtful byte when
        // `report` is given. A step that reads several bytes of one scratch, as a pointer is read,
        // is reported once.
        Doubtful Through(BasicBlock block, Doubtful doubtful, List<Diagnostic>? report)
        {
            var slots = doubtful.Slots;
            foreach (var step in block.Steps)
            {
                if (MemoryAccess.Of(file, step) is not { } access)
                    continue;
                if (report is not null)
                {
                    var read = access.Reads && access.Direct is { } direct ? [direct] : ImmutableArray<Location>.Empty;
                    foreach (var location in read.AddRange(access.Pointer))
                    {
                        if (Slot.Of(location) is not { } slot || Overwriting(slots, slot) is not { } earlier)
                            continue;
                        report.Add(Overwritten(file, step, location, earlier, program) with
                        {
                            Fix = OtherScratch(routine, step, location, program),
                        });
                        break;
                    }
                }
                if (access.Stores && access.Direct is { } stored && Slot.Of(stored) is { } own)
                    slots = slots.Remove(own);
            }

            if (block.Steps.Count == 0 || !(RegisterWalk.CallsAtEnd(block) || block.RunsInto is not null))
                return new Doubtful(slots);
            var call = block.Steps[^1];
            if (report is not null)
            {
                foreach (var callee in Callees(block))
                    Arguments(call, callee, slots, report);
            }

            // A tail call ends the path, so nothing in this routine reads what it stores.
            if (block.EndsInCall)
            {
                foreach (var callee in block.Calls)
                {
                    if (program.Of(callee) is not { } theirs)
                        continue;
                    foreach (var slot in theirs.Stores.Keys.SelectMany(program.Bytes))
                        slots = slots.SetItem(slot, new Call(call, callee));
                }
            }
            return new Doubtful(slots);
        }

        // Reports each doubtful byte that `callee` reads before storing to it, which is an argument
        // an earlier call may have overwritten. Each scratch is reported once at a call, however
        // many of its bytes the callee reads.
        void Arguments(Step call, Symbol callee, ImmutableDictionary<Slot, Call> slots, List<Diagnostic> report)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            var reads = program.Inference.ReadsOf(callee).OrderBy(location => location.Name, StringComparer.Ordinal);
            foreach (var location in reads)
            {
                if (Slot.Of(location) is not { } slot || Overwriting(slots, slot) is not { } earlier
                    || !named.Add(slot.Shown))
                {
                    continue;
                }
                report.Add(Overwritten(file, call, location, earlier, program, callee));
            }
        }
    }

    /// <summary>
    /// Returns the call that may have overwritten <paramref name="slot"/>, or null where no call
    /// has. A call that stores to every byte of the scratch, through an indexed store, counts for
    /// each of its bytes.
    /// </summary>
    private static Call? Overwriting(ImmutableDictionary<Slot, Call> slots, Slot slot) =>
        slots.TryGetValue(slot, out var call) ? call
            : slots.TryGetValue(slot.Whole(), out var whole) ? whole
            : null;

    /// <summary>
    /// Returns the warning for a read at <paramref name="step"/> of <paramref name="location"/>,
    /// which <paramref name="call"/> may have overwritten. The warning names the routine that
    /// stores to it where that is not the routine called, and points at the call and the store.
    /// </summary>
    /// <param name="file">The file the read is in.</param>
    /// <param name="step">The read, or the call to the routine that reads the scratch.</param>
    /// <param name="location">The byte of scratch read.</param>
    /// <param name="call">The earlier call that may have overwritten it.</param>
    /// <param name="program">The routines of the program and the scratch each stores to.</param>
    /// <param name="reader">
    /// The routine called at <paramref name="step"/> that reads the scratch, or null for a read in
    /// the routine itself.
    /// </param>
    private static Diagnostic Overwritten(
        FileAnalysis file, Step step, Location location, Call call, ProgramScratch program, Symbol? reader = null)
    {
        var theirs = program.Of(call.Callee)!.Stores;
        var slot = Slot.Of(location)!.Value;
        var store = theirs.TryGetValue(slot, out var exact) ? exact : theirs[slot.Whole()];
        var through = store.Routine == call.Callee.DisplayName ? "" : $", through `{store.Routine}`";
        var before = reader is null ? "" : $" before `{reader.DisplayName}` reads it";
        var (span, related) = Reported(file, step);
        var message = Catalogue.ScratchOverwritten.Message(location.Name, call.Callee.DisplayName, through, before);
        return new Diagnostic(span, message, [
            .. related,
            new RelatedSpan(Reported(file, call.Step).Span, $"the call to `{call.Callee.DisplayName}`"),
            new RelatedSpan(store.Span, $"`{store.Routine}` stores to `{location.Name}` here"),
        ]);
    }

    /// <summary>
    /// Returns the fix that moves <paramref name="routine"/> from the scratch read at
    /// <paramref name="step"/> to other scratch, or null where no move is safe. The fix renames the
    /// routine's own references, so it is offered only where the scratch is the routine's own
    /// working storage. That means it is not the routine's argument, it is not an argument the
    /// routine passes on, and it is not used from a macro body. The other scratch must be as large,
    /// unused by the routine, neither stored to nor read by any routine it calls, and reachable by
    /// name wherever the routine names the first. A routine an interrupt handler runs may use no
    /// scratch at all, so it is offered none.
    /// </summary>
    private static DiagnosticFix? OtherScratch(Routine routine, Step step, Location location, ProgramScratch program)
    {
        var (file, model) = (routine.File, routine.File.Model);
        if (location.Root is not { } scratch || Slot.Of(location) is not { } read || Reported(file, step).Related.Count > 0
            || program.Handlers.ContainsKey(routine.Key))
        {
            return null;
        }
        var group = read.Whole();
        if (Groups(program.Inference.ReadsOf(routine.Symbol)).Contains(group)
            || routine.Callees.Any(callee => Groups(program.Inference.ReadsOf(callee)).Contains(group)))
        {
            return null;
        }

        // Every use of the scratch must be in the routine's own block, where the rename reaches.
        if (Block(routine) is not { } block)
            return null;
        foreach (var other in routine.Region.Blocks.SelectMany(each => each.Steps))
        {
            var named = MemoryAccess.Of(file, other) is { } access
                && Named(access).Any(at => Slot.Of(at)?.Whole() == group);
            if (named && (other.Statement.Tree != model.Tree || !block.FullSpan.Contains(other.Statement.Span)))
                return null;
        }
        var names = block.DescendantNodes().OfType<NameExpressionSyntax>()
            .Where(name => model.SymbolOf(name) is { } symbol && Same(symbol, scratch))
            .ToList();
        if (names.Count == 0)
            return null;

        var calleeReads = routine.Callees.SelectMany(callee => Groups(program.Inference.ReadsOf(callee))).ToHashSet();
        foreach (var candidate in program.Scratch)
        {
            var other = Slot.WholeOf(candidate);
            if (other == group || (candidate.Size ?? 0) < (scratch.Size ?? long.MaxValue)
                || routine.Stores.Keys.Any(slot => slot.Whole() == other) || routine.Uses.ContainsKey(other)
                || calleeReads.Contains(other))
            {
                continue;
            }
            if (names.All(name => Reaches(model, name, scratch, candidate)))
            {
                var spans = names.Select(name => model.Tree.GetSpan(name.Parts[name.Parts.Count - 1].Span)).ToList();
                return new DiagnosticFix(FixKind.UseScratch, candidate.Name, Spans: spans);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns whether writing <paramref name="candidate"/>'s name in place of the last part of
    /// <paramref name="name"/>, which names <paramref name="scratch"/>, names
    /// <paramref name="candidate"/>. A qualified name reaches it where the two are declared in the
    /// same scope, and a plain name where a lookup at that point finds it.
    /// </summary>
    private static bool Reaches(SemanticModel model, NameExpressionSyntax name, Symbol scratch, Symbol candidate) =>
        name.Parts.Count > 1
            ? Scope(scratch) == Scope(candidate) && scratch.Tree.Path == candidate.Tree.Path
            : model.LookupSymbols(name.Position, candidate.Name).Any(found => Same(found, candidate));

    /// <summary>Returns the qualified name of the scope <paramref name="symbol"/> is declared in.</summary>
    private static string Scope(Symbol symbol)
    {
        var qualified = symbol.QualifiedName;
        var last = qualified.LastIndexOf("::", StringComparison.Ordinal);
        return last < 0 ? "" : qualified[..last];
    }

    /// <summary>Returns the block of the <c>.proc</c> that declares <paramref name="routine"/>, or null.</summary>
    private static BlockSyntax? Block(Routine routine)
    {
        var symbol = routine.Symbol;
        if (symbol.Tree != routine.File.Model.Tree)
            return null;
        return symbol.Tree.Root.FindToken(symbol.NameSpan.Start).Parent?.AncestorsAndSelf()
            .OfType<BlockSyntax>()
            .FirstOrDefault(block => block.BlockKind == BlockKind.Proc);
    }

    /// <summary>
    /// Returns the locations <paramref name="access"/> names: the one it reaches directly, the one an
    /// index starts from, and the bytes of a pointer it reads.
    /// </summary>
    private static IEnumerable<Location> Named(MemoryAccess access) =>
        new[] { access.Direct, access.Indexed }.OfType<Location>().Concat(access.Pointer);

    /// <summary>Returns the scratch that <paramref name="locations"/> are bytes of, as whole slots.</summary>
    private static HashSet<Slot> Groups(IEnumerable<Location> locations) =>
        [.. locations.Select(Slot.Of).OfType<Slot>().Select(slot => slot.Whole())];

    /// <summary>
    /// Returns an error for each scratch that a routine an interrupt handler runs uses, once for
    /// each routine and scratch, at the routine's first use of it. The error points at the handler.
    /// </summary>
    private static IEnumerable<Diagnostic> InHandlers(ProgramScratch program)
    {
        foreach (var routine in program.Routines.Values)
        {
            if (!program.Handlers.TryGetValue(routine.Key, out var handler))
                continue;
            var runs = Same(routine.Symbol, handler)
                ? ""
                : $": `{routine.Symbol.DisplayName}` runs under the interrupt handler `{handler.DisplayName}`";
            foreach (var (group, use) in routine.Uses)
            {
                yield return new Diagnostic(
                    use.Span,
                    Catalogue.ScratchInHandler.Message(group.Shown, runs),
                    [new RelatedSpan(handler.DeclarationSpan, $"the interrupt handler `{handler.DisplayName}`")]);
            }
        }
    }

    /// <summary>
    /// Returns where a diagnostic about <paramref name="step"/> is shown. A line of a macro body
    /// is shown at the call that expanded it, with the body line beside it, because the call is
    /// what the programmer wrote in this routine.
    /// </summary>
    private static (Span Span, IReadOnlyList<RelatedSpan> Related) Reported(FileAnalysis file, Step step)
    {
        var node = step.Statement;
        var inBody = node.Tree != file.Model.Tree;
        MacroCallSyntax? call = null;
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is null)
                continue;
            call = level.Call;
            if (level.Body is { } body && body.Tree == node.Tree
                && node.Position >= body.Position && node.Position < body.FullSpan.End)
            {
                inBody = true;
            }
        }
        return !inBody || call is null
            ? (node.Tree.GetSpan(node.Span), [])
            : (call.Tree.GetSpan(call.Span), [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]);
    }

    /// <summary>
    /// Returns the routines a call or a jump at the end of <paramref name="block"/> passes control
    /// to, together with the routine a <c>.fallthrough</c> runs into.
    /// </summary>
    private static IEnumerable<Symbol> Callees(BasicBlock block) =>
        block.RunsInto is { } into ? block.Calls.Append(into) : block.Calls;

    /// <summary>Returns the key of the routine a call to <paramref name="target"/> runs.</summary>
    private static RoutineKey KeyOf(Symbol target) => RoutineKey.Of(RegisterWalk.Owner(target) ?? target);

    /// <summary>Returns whether two symbols are the same declaration, which may be seen from two models.</summary>
    private static bool Same(Symbol one, Symbol other) => one.Tree.Path == other.Tree.Path && one.FlatName == other.FlatName;

    /// <summary>
    /// Identifies a byte of scratch by the file and the flattened name of its declaration, and its
    /// offset. A null offset stands for every byte of the scratch. A key by name, rather than by
    /// symbol, matches the same scratch in a file kept from before an edit.
    /// </summary>
    /// <param name="Path">The path of the file that declares the scratch.</param>
    /// <param name="Name">The flattened name of the scratch.</param>
    /// <param name="Offset">The byte's offset from the start of the scratch, or null for every byte.</param>
    /// <param name="Shown">The name of the scratch as a reader sees it.</param>
    private readonly record struct Slot(string Path, string Name, long? Offset, string Shown)
    {
        /// <summary>Returns the byte of scratch <paramref name="location"/> names, or null where it names none.</summary>
        public static Slot? Of(Location location) =>
            Location.IsScratch(location.Root)
                ? new Slot(location.Root!.Tree.Path, location.Root.FlatName, location.Offset, location.Root.DisplayName)
                : null;

        /// <summary>Returns the slot that stands for every byte of the scratch <paramref name="scratch"/>.</summary>
        public static Slot WholeOf(Symbol scratch) => new(scratch.Tree.Path, scratch.FlatName, null, scratch.DisplayName);

        /// <summary>Returns the slot that stands for every byte of this one's scratch.</summary>
        public Slot Whole() => this with { Offset = null };
    }

    /// <summary>Represents one store to or use of scratch, which shows that a routine reaches it.</summary>
    /// <param name="Span">Where the store or use is shown.</param>
    /// <param name="Routine">The name of the routine the store or use is in.</param>
    private sealed record Store(Span Span, string Routine);

    /// <summary>Represents a call that may have overwritten scratch.</summary>
    /// <param name="Step">The call.</param>
    /// <param name="Callee">The routine it calls.</param>
    private sealed record Call(Step Step, Symbol Callee);

    /// <summary>
    /// Represents one routine as the scratch checks see it: the scratch it stores to and uses
    /// itself, the routines it passes control to, and the scratch it may store to through them.
    /// </summary>
    private sealed class Routine(RoutineKey key, FlowRegion region, FileAnalysis file)
    {
        /// <summary>Gets the key that identifies the routine.</summary>
        public RoutineKey Key { get; } = key;

        /// <summary>Gets the routine's region.</summary>
        public FlowRegion Region { get; } = region;

        /// <summary>Gets the file the routine is in.</summary>
        public FileAnalysis File { get; } = file;

        /// <summary>Gets the routine's symbol.</summary>
        public Symbol Symbol => Region.Routine;

        /// <summary>Gets each byte of scratch the routine stores to itself, with one store that shows it.</summary>
        public Dictionary<Slot, Store> OwnStores { get; } = [];

        /// <summary>
        /// Gets each scratch the routine reads or stores to itself, as a whole slot, with one use
        /// that shows it.
        /// </summary>
        public Dictionary<Slot, Store> Uses { get; } = [];

        /// <summary>
        /// Gets the routines the routine passes control to, by a call, a jump, a branch or a
        /// <c>.fallthrough</c>.
        /// </summary>
        public List<Symbol> Callees { get; } = [];

        /// <summary>
        /// Gets each byte of scratch the routine may store to, directly or through the routines
        /// it passes control to, with one store that shows it.
        /// </summary>
        public Dictionary<Slot, Store> Stores { get; } = [];
    }

    /// <summary>
    /// Represents what the scratch checks know about the whole program: its routines, which of
    /// them run under an interrupt handler, and every scratch declared.
    /// </summary>
    private sealed class ProgramScratch
    {
        // The size of each scratch whose size is known, by the file and the flattened name of its
        // declaration. Zero page holds at most 256 bytes, so a larger size is not expanded.
        private readonly Dictionary<(string Path, string Name), long> sizes = [];

        /// <summary>
        /// Initializes the routines of <paramref name="files"/>, finds what each stores to and uses,
        /// and works out which run under which handler.
        /// </summary>
        public ProgramScratch(IReadOnlyList<FileAnalysis> files, MemoryInference inference)
        {
            Inference = inference;
            foreach (var file in files)
            {
                var walk = new RegisterWalk(file.Model, file.Layout, file.Flow, file.State);
                foreach (var region in file.Flow.Regions)
                {
                    var key = RoutineKey.Of(region.Routine);
                    if (!Routines.ContainsKey(key))
                        Routines[key] = Read(new Routine(key, region, file), walk);
                }
            }
            Scratch = [.. files.SelectMany(file => file.Model.Symbols.Where(Location.IsScratch))
                .DistinctBy(symbol => (symbol.Tree.Path, symbol.FlatName))];
            foreach (var scratch in Scratch)
            {
                if (scratch.Size is { } size and > 0 and <= 256)
                    sizes[(scratch.Tree.Path, scratch.FlatName)] = size;
            }
            Spread();
            foreach (var handler in Routines.Values.Where(routine => routine.Symbol.Signature?.IsInterrupt == true))
            {
                foreach (var reached in Reach(handler))
                    Handlers.TryAdd(reached.Key, handler.Symbol);
            }
        }

        /// <summary>Gets the routines of the program, by key, in the order of their files.</summary>
        public Dictionary<RoutineKey, Routine> Routines { get; } = [];

        /// <summary>Gets what each routine reads before storing to it.</summary>
        public MemoryInference Inference { get; }

        /// <summary>Gets every scratch the program declares, in the order of its files.</summary>
        public IReadOnlyList<Symbol> Scratch { get; }

        /// <summary>
        /// Returns the bytes <paramref name="slot"/> stands for. A slot for every byte of a scratch,
        /// which an indexed store makes, is each of its bytes, so that a later store to one of them
        /// makes that byte alone the routine's own again. Where the scratch's size is not known, the
        /// slot stays as it is.
        /// </summary>
        public IEnumerable<Slot> Bytes(Slot slot)
        {
            if (slot.Offset is not null || !sizes.TryGetValue((slot.Path, slot.Name), out var size))
                return [slot];
            return Enumerable.Range(0, (int)size).Select(offset => slot with { Offset = offset });
        }

        /// <summary>Gets each routine an interrupt handler runs, with that handler.</summary>
        public Dictionary<RoutineKey, Symbol> Handlers { get; } = [];

        /// <summary>Returns the routine a call to <paramref name="target"/> runs, or null where it is not in the program.</summary>
        public Routine? Of(Symbol target) => Routines.GetValueOrDefault(KeyOf(target));

        /// <summary>Reads what <paramref name="routine"/> stores to and uses itself, and where it passes control.</summary>
        private static Routine Read(Routine routine, RegisterWalk walk)
        {
            var file = routine.File;
            foreach (var block in routine.Region.Blocks)
            {
                foreach (var step in block.Steps)
                {
                    if (MemoryAccess.Of(file, step) is not { } access)
                        continue;
                    var shown = new Store(Reported(file, step).Span, routine.Symbol.DisplayName);
                    foreach (var slot in Named(access).Select(Slot.Of).OfType<Slot>())
                        routine.Uses.TryAdd(slot.Whole(), shown);
                    if (!access.Stores)
                        continue;
                    var stored = access.Direct is { } direct ? Slot.Of(direct)
                        : access.Indexed is { } start ? Slot.Of(start)?.Whole()
                        : null;
                    if (stored is { } own)
                        routine.OwnStores.TryAdd(own, shown);
                }
                routine.Callees.AddRange(Callees(block));
                routine.Callees.AddRange(walk.Leaves(block, routine.Symbol));
            }
            foreach (var (slot, store) in routine.OwnStores)
                routine.Stores[slot] = store;
            return routine;
        }

        /// <summary>
        /// Has each routine take on what the routines it passes control to store, until nothing
        /// changes. A routine on a cycle of calls therefore takes on what every routine of the
        /// cycle stores.
        /// </summary>
        private void Spread()
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var routine in Routines.Values)
                {
                    foreach (var callee in routine.Callees)
                    {
                        if (Of(callee) is not { } theirs || theirs == routine)
                            continue;
                        foreach (var (slot, store) in theirs.Stores)
                            changed |= routine.Stores.TryAdd(slot, store);
                    }
                }
            }
        }

        /// <summary>
        /// Returns every routine that <paramref name="start"/> reaches by a call, a jump, a branch or
        /// a <c>.fallthrough</c>, itself included.
        /// </summary>
        private List<Routine> Reach(Routine start)
        {
            var seen = new HashSet<RoutineKey>();
            var pending = new Stack<Routine>([start]);
            var found = new List<Routine>();
            while (pending.TryPop(out var routine))
            {
                if (!seen.Add(routine.Key))
                    continue;
                found.Add(routine);
                foreach (var callee in routine.Callees)
                {
                    if (Of(callee) is { } next)
                        pending.Push(next);
                }
            }
            return found;
        }
    }

    /// <summary>
    /// Represents the bytes of scratch at one point that a call may have overwritten since the
    /// routine last stored them, each with one such call.
    /// </summary>
    private sealed class Doubtful(ImmutableDictionary<Slot, Call> slots) : IEquatable<Doubtful>
    {
        /// <summary>Gets the state where nothing is doubtful, as at a routine's entry.</summary>
        public static Doubtful Nothing { get; } = new(ImmutableDictionary<Slot, Call>.Empty);

        /// <summary>Gets each doubtful byte, with a call that may have overwritten it.</summary>
        public ImmutableDictionary<Slot, Call> Slots { get; } = slots;

        /// <summary>
        /// Returns what is doubtful where two paths meet, which is what is doubtful on either. A
        /// byte doubtful on both keeps the call the earlier path found.
        /// </summary>
        public static Doubtful Merge(Doubtful? known, Doubtful arriving)
        {
            if (known is null)
                return arriving;
            var slots = known.Slots;
            foreach (var (slot, call) in arriving.Slots)
            {
                if (!slots.ContainsKey(slot))
                    slots = slots.Add(slot, call);
            }
            return new Doubtful(slots);
        }

        /// <inheritdoc/>
        public bool Equals(Doubtful? other) =>
            other is not null && Slots.Count == other.Slots.Count && Slots.Keys.All(other.Slots.ContainsKey);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as Doubtful);

        /// <inheritdoc/>
        public override int GetHashCode() => Slots.Count;
    }
}
