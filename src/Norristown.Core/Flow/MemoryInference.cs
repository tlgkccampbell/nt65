using System.Collections.Immutable;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Infers which locations in memory each routine reads and may write, for the memory part of
/// <see cref="InputSources"/>. A location is one a routine reads when some path through it loads
/// the location before storing to it, directly or through a routine it calls. Only a direct operand
/// on a resolved symbol counts, so an indexed or indirect read is not an input. Each byte a direct
/// access reaches is a location of its own, so a 16-bit store to <c>ptr</c> on the 65816 writes
/// <c>ptr</c> and <c>ptr+1</c>.
/// <para>
/// The answer is worked out on demand, for the routines a question reaches, and kept for the rest
/// of the question. It is a best guess for showing, and no check warns from it. A routine that
/// calls itself directly is taken to read and write nothing more through that call, since what the
/// call does is what the routine does. A routine whose body is not in the program, one reached
/// again through another routine it calls, and one that stores through a pointer may write any
/// location, as <see cref="WritesAnything"/> says.
/// </para>
/// </summary>
internal sealed class MemoryInference
{
    private readonly ProgramAnalysis analysis;
    private readonly Dictionary<RoutineKey, (FlowRegion Region, FileAnalysis File)> routines = [];
    private readonly Dictionary<RoutineKey, Inferred> found = [];
    private readonly HashSet<RoutineKey> active = [];
    private readonly Stack<RoutineKey> working = [];
    private readonly Dictionary<FileAnalysis, RegisterWalk> walks = new(ReferenceEqualityComparer.Instance);

    /// <summary>Initializes an inference over every routine of <paramref name="analysis"/>'s program.</summary>
    public MemoryInference(ProgramAnalysis analysis)
    {
        this.analysis = analysis;
        foreach (var file in analysis.Files)
        {
            foreach (var region in file.Flow.Regions)
                routines.TryAdd(RoutineKey.Of(region.Routine), (region, file));
        }
    }

    /// <summary>
    /// Returns the locations a call to <paramref name="target"/> reads before it writes them. A
    /// label is taken as the routine it is in.
    /// </summary>
    public ImmutableHashSet<Location> ReadsOf(Symbol target) => Of(target).Reads;

    /// <summary>
    /// Returns the locations a call to <paramref name="target"/> may write. An indexed store counts
    /// as a write of the location it starts from.
    /// </summary>
    public ImmutableHashSet<Location> WritesOf(Symbol target) => Of(target).Writes;

    /// <summary>
    /// Returns the indexed stores that a call to <paramref name="target"/> may make, directly or
    /// through a routine it calls. Each says where else than its start such a store may land.
    /// </summary>
    public ImmutableHashSet<IndexedStore> IndexedStoresOf(Symbol target) => Of(target).Indexed;

    /// <summary>
    /// Returns whether a call to <paramref name="target"/> may write any location at all. That is
    /// so where the routine's body is not in the program, or where the call reaches the routine
    /// again through another routine. It is also so where the routine stores through a pointer,
    /// calls somewhere nt65 cannot identify, or runs <c>brk</c> or <c>cop</c>, directly or through
    /// a routine it calls.
    /// </summary>
    public bool WritesAnything(Symbol target) => Of(target).Anything;

    /// <summary>
    /// Returns the location that <paramref name="location"/> stands for once each address alias is
    /// followed to the location its value names. <c>.data TEMP3 = FNCNAM</c> makes <c>TEMP3+1</c>
    /// stand for <c>FNCNAM+1</c>, so two spellings of one byte are found to be one.
    /// </summary>
    public Location Anchor(Location location)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (location.Root is not { Kind: SymbolKind.AddressAlias, ValueExpression: { } value } alias
                || alias.Value.AsNumber() is not null
                || analysis.ModelFor(alias.Tree.Path) is not { } model
                || Location.Of(model, value, null) is not { } named)
            {
                break;
            }
            location = named with { Offset = named.Offset + location.Offset };
        }
        return location;
    }

    /// <summary>
    /// Returns whether two different spellings may stand for the same byte. They do where both
    /// follow to the same location through their address aliases, or where the source fixes both
    /// addresses and they are equal.
    /// </summary>
    public bool Overlaps(Location a, Location b)
    {
        if (a == b)
            return false;
        var (x, y) = (Anchor(a), Anchor(b));
        return x == y || (x.Address is { } at && at == y.Address);
    }

    /// <summary>
    /// Returns the locations a call to <paramref name="target"/> writes on every path that returns,
    /// directly or through a routine it calls. After such a call, the call is where a location's
    /// value came from, not just something that may have changed it.
    /// </summary>
    public ImmutableHashSet<Location> AlwaysWrittenBy(Symbol target) => Of(target).Always;

    /// <summary>
    /// Returns what a call to <paramref name="target"/> reads, may write and always writes, worked
    /// out the first time it is asked. A routine whose body is not in the program may write
    /// anything. So may one that is already being worked out further up the calls, unless it is
    /// the routine being worked out now, whose own call adds nothing to what it does.
    /// </summary>
    private Inferred Of(Symbol target)
    {
        var key = RoutineKey.Of(RegisterWalk.Owner(target) ?? target);
        if (found.TryGetValue(key, out var known))
            return known;
        if (!routines.TryGetValue(key, out var routine))
            return Inferred.Unseen;
        if (!active.Add(key))
            return working.Peek() == key ? Inferred.Nothing : Inferred.Unseen;
        working.Push(key);
        var inferred = Infer(routine.Region, routine.File);
        working.Pop();
        active.Remove(key);
        found[key] = inferred;
        return inferred;
    }

    /// <summary>
    /// Returns what one routine reads before writing, what it may write and what it always writes.
    /// The blocks are run to a fixed point over the locations every path has stored to, and then
    /// walked once more to collect the reads, because a read counts only against what is stored on
    /// every path to it. What every path has stored where it returns is what the routine always
    /// writes. A path that leaves for another routine without a call, through a branch, a jump
    /// into its body or a <c>.fallthrough</c>, returns from that routine, so it takes on what
    /// that routine reads and writes, as a tail call does.
    /// </summary>
    private Inferred Infer(FlowRegion region, FileAnalysis file)
    {
        var blocks = region.Blocks;
        var reads = ImmutableHashSet.CreateBuilder<Location>();
        var writes = ImmutableHashSet.CreateBuilder<Location>();
        var indexed = ImmutableHashSet.CreateBuilder<IndexedStore>();
        var anything = false;
        var solver = new Dataflow<Stored>(
            blocks, (block, stored) => Through(block, stored, null, null), Stored.Merge, block => ControlFlow.Onward(blocks, block));
        if (blocks.Count == 0)
            return Inferred.Nothing;
        solver.Enter(0, Stored.Nothing);
        ImmutableHashSet<Location>? always = null;
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is not { } stored)
                continue;
            var after = Through(block, stored, reads, writes);
            if (block.End is BlockEnd.Return or BlockEnd.TailCall)
                always = always is null ? after.Locations : always.Intersect(after.Locations);
            foreach (var target in WalkOf(file).Leaves(block, region.Routine))
            {
                var into = Of(target);
                reads.UnionWith(into.Reads.Where(location => !after.Locations.Contains(location)));
                writes.UnionWith(into.Writes);
                indexed.UnionWith(into.Indexed);
                anything |= into.Anything;
                var left = after.Locations.Union(into.Always);
                always = always is null ? left : always.Intersect(left);
            }
        }
        return new Inferred(reads.ToImmutable(), writes.ToImmutable(), always ?? [], indexed.ToImmutable(), anything);

        // The solver's passes only find what every path has stored. What the routine may write is
        // collected on the last pass, the one that is given the builders.
        Stored Through(BasicBlock block, Stored stored, ImmutableHashSet<Location>.Builder? read, ImmutableHashSet<Location>.Builder? written)
        {
            var locations = stored.Locations;
            var collect = written is not null;
            foreach (var step in block.Steps)
            {
                // An interrupt the routine raises runs a handler that may write anything.
                if (step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Brk or MnemonicKind.Cop })
                    anything |= collect;
                if (MemoryAccess.Of(file, step) is not { } access)
                    continue;
                // A direct access reaches each of its bytes, so a 16-bit one reads or writes the
                // byte after the one it names as well, and one of unknown width may.
                foreach (var loaded in access.Reads ? access.ReachedBytes.AddRange(access.Pointer) : access.Pointer)
                {
                    if (!locations.Contains(loaded))
                        read?.Add(loaded);
                }
                if (!access.Stores)
                    continue;
                if (access.Direct is not null)
                {
                    locations = locations.Union(access.DirectBytes);
                    written?.UnionWith(access.ReachedBytes);
                }
                else if (access.IndexedStore is { } store)
                {
                    written?.Add(store.Start);
                    if (collect)
                        indexed.Add(store);
                }
                else if (access.Indirect)
                {
                    anything |= collect;
                }
            }
            if (RegisterWalk.CallsAtEnd(block))
            {
                // A call through a pointer whose `.next` names several routines has stored only
                // what every one of them always stores.
                ImmutableHashSet<Location>? stores = null;
                foreach (var callee in block.Calls)
                {
                    var called = Of(callee);
                    foreach (var location in called.Reads)
                    {
                        if (!locations.Contains(location))
                            read?.Add(location);
                    }
                    written?.UnionWith(called.Writes);
                    if (collect)
                    {
                        indexed.UnionWith(called.Indexed);
                        anything |= called.Anything;
                    }
                    stores = stores is null ? called.Always : stores.Intersect(called.Always);
                }
                if (!block.CallsUnknown && stores is not null)
                    locations = locations.Union(stores);
            }

            // A call or a jump nt65 cannot follow may run code that writes anything.
            anything |= collect && block.CallsUnknown;
            return new Stored(locations);
        }
    }

    /// <summary>Returns the register walk over <paramref name="file"/>, which finds where a path leaves a routine.</summary>
    private RegisterWalk WalkOf(FileAnalysis file)
    {
        if (!walks.TryGetValue(file, out var walk))
        {
            walk = new RegisterWalk(file.Model, file.Layout, file.Flow, file.State);
            walks[file] = walk;
        }
        return walk;
    }

    /// <summary>Represents what one routine reads, may write and always writes.</summary>
    /// <param name="Reads">The locations it reads before it writes them.</param>
    /// <param name="Writes">The locations it may write, counting an indexed store as a write of where it starts.</param>
    /// <param name="Always">The locations it writes on every path that returns.</param>
    /// <param name="Indexed">The indexed stores it may make.</param>
    /// <param name="Anything">Whether it may write any location at all.</param>
    private sealed record Inferred(
        ImmutableHashSet<Location> Reads, ImmutableHashSet<Location> Writes, ImmutableHashSet<Location> Always,
        ImmutableHashSet<IndexedStore> Indexed, bool Anything)
    {
        /// <summary>Gets what a call that adds nothing to what is known reads and writes, which is nothing.</summary>
        public static Inferred Nothing { get; } = new([], [], [], [], false);

        /// <summary>
        /// Gets what a routine nothing can be seen of reads and writes. It reads nothing that can
        /// be named, and it may write anything.
        /// </summary>
        public static Inferred Unseen { get; } = new([], [], [], [], true);
    }

    /// <summary>Represents the locations every path to a point has stored to.</summary>
    private sealed class Stored(ImmutableHashSet<Location> locations) : IEquatable<Stored>
    {
        /// <summary>Gets what is stored where a routine is entered, which is nothing.</summary>
        public static Stored Nothing { get; } = new([]);

        /// <summary>Gets the locations every path has stored to.</summary>
        public ImmutableHashSet<Location> Locations { get; } = locations;

        /// <summary>Returns what two paths that meet have both stored to.</summary>
        public static Stored Merge(Stored? known, Stored arriving) =>
            known is null ? arriving : new Stored(known.Locations.Intersect(arriving.Locations));

        /// <inheritdoc/>
        public bool Equals(Stored? other) => other is not null && Locations.SetEquals(other.Locations);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as Stored);

        /// <inheritdoc/>
        public override int GetHashCode() => Locations.Count;
    }
}
