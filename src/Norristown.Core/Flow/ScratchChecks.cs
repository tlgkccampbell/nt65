using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Reports reads of scratch that a call may have overwritten since the routine stored it.
/// Scratch is data that <c>.scratch</c> declares. It holds a routine's working values and never
/// carries a value into or out of a call, so routines can share its bytes as long as no routine
/// relies on them across a call to another routine that writes them.
/// <para>
/// The check follows only instructions that name scratch directly, never what memory holds. A
/// direct store to a byte of scratch makes it the routine's own again. A call to a routine that
/// may store to the byte, directly or through a routine it calls in turn, makes it doubtful. A
/// read of a doubtful byte is reported. An indexed store counts as a store to every byte of the
/// scratch it names, and a call nt65 cannot follow is taken to store nothing.
/// </para>
/// </summary>
internal static class ScratchChecks
{
    /// <summary>
    /// Returns a warning for each read of scratch that a call may have overwritten, over every
    /// routine of <paramref name="files"/>. A program that declares no scratch is not walked.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Check(IReadOnlyList<FileAnalysis> files)
    {
        if (!files.Any(file => file.Model.Symbols.Any(Location.IsScratch)))
            return [];

        var routines = new Dictionary<RoutineKey, (FlowRegion Region, FileAnalysis File)>();
        foreach (var file in files)
        {
            foreach (var region in file.Flow.Regions)
                routines.TryAdd(RoutineKey.Of(region.Routine), (region, file));
        }
        var writes = Writes(routines);

        var found = new List<Diagnostic>();
        var reported = new HashSet<(Span, string)>();
        foreach (var (region, file) in routines.Values)
        {
            foreach (var diagnostic in Walk(region, file, writes))
            {
                if (reported.Add((diagnostic.Span, diagnostic.Message)))
                    found.Add(diagnostic);
            }
        }
        return found;
    }

    /// <summary>
    /// Returns the scratch each routine may store to, directly or through the routines it calls,
    /// with one store that shows it. The stores a routine makes itself are found first, and each
    /// routine then takes on what its callees store, until nothing changes. A routine on a cycle
    /// of calls therefore takes on what every routine of the cycle stores.
    /// </summary>
    private static Dictionary<RoutineKey, Dictionary<Slot, Store>> Writes(
        Dictionary<RoutineKey, (FlowRegion Region, FileAnalysis File)> routines)
    {
        var writes = new Dictionary<RoutineKey, Dictionary<Slot, Store>>();
        var callees = new Dictionary<RoutineKey, List<RoutineKey>>();
        foreach (var (key, (region, file)) in routines)
        {
            var own = new Dictionary<Slot, Store>();
            var called = new List<RoutineKey>();
            foreach (var block in region.Blocks)
            {
                foreach (var step in block.Steps)
                {
                    if (MemoryAccess.Of(file, step) is not { Stores: true } access)
                        continue;
                    var slot = access.Direct is { } direct ? Slot.Of(direct)
                        : access.Indexed is { } start ? Slot.Of(start)?.Whole()
                        : null;
                    if (slot is { } stored)
                        own.TryAdd(stored, new Store(Reported(file, step).Span, region.Routine.DisplayName));
                }
                foreach (var callee in Callees(block))
                    called.Add(KeyOf(callee));
            }
            writes[key] = own;
            callees[key] = called;
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (key, called) in callees)
            {
                var own = writes[key];
                foreach (var callee in called)
                {
                    if (!writes.TryGetValue(callee, out var theirs) || ReferenceEquals(theirs, own))
                        continue;
                    foreach (var (slot, store) in theirs)
                        changed |= own.TryAdd(slot, store);
                }
            }
        }
        return writes;
    }

    /// <summary>
    /// Returns a warning for each read in <paramref name="region"/> of scratch that a call may
    /// have overwritten since the routine last stored it. The routine is entered at its start and
    /// at each label a <c>.state</c> declares an entry point, with nothing doubtful.
    /// </summary>
    private static List<Diagnostic> Walk(
        FlowRegion region, FileAnalysis file, Dictionary<RoutineKey, Dictionary<Slot, Store>> writes)
    {
        var found = new List<Diagnostic>();
        var blocks = region.Blocks;
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
                        if (Slot.Of(location) is not { } slot || Overwriting(slots, slot) is not { } call)
                            continue;
                        report.Add(Overwritten(file, step, location, call, writes));
                        break;
                    }
                }
                if (access.Stores && access.Direct is { } stored && Slot.Of(stored) is { } own)
                    slots = slots.Remove(own);
            }

            // A tail call ends the path, so nothing in this routine reads what it stores.
            if (block.EndsInCall)
            {
                var call = block.Steps[^1];
                foreach (var callee in block.Calls)
                {
                    if (!writes.TryGetValue(KeyOf(callee), out var theirs))
                        continue;
                    foreach (var slot in theirs.Keys)
                        slots = slots.SetItem(slot, new Call(call, callee));
                }
            }
            return new Doubtful(slots);
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
    private static Diagnostic Overwritten(
        FileAnalysis file, Step step, Location location, Call call,
        Dictionary<RoutineKey, Dictionary<Slot, Store>> writes)
    {
        var theirs = writes[KeyOf(call.Callee)];
        var slot = Slot.Of(location)!.Value;
        var store = theirs.TryGetValue(slot, out var exact) ? exact : theirs[slot.Whole()];
        var through = store.Routine == call.Callee.DisplayName ? "" : $", through `{store.Routine}`";
        var (span, related) = Reported(file, step);
        var message = Catalogue.ScratchOverwritten.Message(location.Name, call.Callee.DisplayName, through);
        return new Diagnostic(span, message, [
            .. related,
            new RelatedSpan(Reported(file, call.Step).Span, $"the call to `{call.Callee.DisplayName}`"),
            new RelatedSpan(store.Span, $"`{store.Routine}` stores to `{location.Name}` here"),
        ]);
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

    /// <summary>
    /// Identifies a byte of scratch by the file and the flattened name of its declaration, and its
    /// offset. A null offset stands for every byte of the scratch. A key by name, rather than by
    /// symbol, matches the same scratch in a file kept from before an edit.
    /// </summary>
    /// <param name="Path">The path of the file that declares the scratch.</param>
    /// <param name="Name">The flattened name of the scratch.</param>
    /// <param name="Offset">The byte's offset from the start of the scratch, or null for every byte.</param>
    private readonly record struct Slot(string Path, string Name, long? Offset)
    {
        /// <summary>Returns the byte of scratch <paramref name="location"/> names, or null where it names none.</summary>
        public static Slot? Of(Location location) =>
            Location.IsScratch(location.Root)
                ? new Slot(location.Root!.Tree.Path, location.Root.FlatName, location.Offset)
                : null;

        /// <summary>Returns the slot that stands for every byte of this one's scratch.</summary>
        public Slot Whole() => this with { Offset = null };
    }

    /// <summary>Represents one store to scratch, which shows that a routine may write it.</summary>
    /// <param name="Span">Where the store is shown.</param>
    /// <param name="Routine">The name of the routine the store is in.</param>
    private sealed record Store(Span Span, string Routine);

    /// <summary>Represents a call that may have overwritten scratch.</summary>
    /// <param name="Step">The call.</param>
    /// <param name="Callee">The routine it calls.</param>
    private sealed record Call(Step Step, Symbol Callee);

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
