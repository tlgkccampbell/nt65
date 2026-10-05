using System.Collections.Immutable;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Infers which locations in memory each routine reads and may write, for the memory part of
/// <see cref="InputSources"/>. A location is one a routine reads when some path through it loads
/// the location before storing to it, directly or through a routine it calls. Only a direct operand
/// on a resolved symbol counts, so an indexed or indirect read is not an input.
/// <para>
/// The answer is worked out on demand, for the routines a question reaches, and kept for the rest
/// of the question. It is a best guess for showing, and no check warns from it. A routine that
/// calls itself, directly or not, is taken to read nothing more through that call.
/// </para>
/// </summary>
internal sealed class MemoryInference
{
    private readonly Dictionary<RoutineKey, (FlowRegion Region, FileAnalysis File)> routines = [];
    private readonly Dictionary<RoutineKey, Inferred> found = [];
    private readonly HashSet<RoutineKey> active = [];
    private readonly Dictionary<FileAnalysis, RegisterWalk> walks = new(ReferenceEqualityComparer.Instance);

    /// <summary>Initializes an inference over every routine of <paramref name="analysis"/>'s program.</summary>
    public MemoryInference(ProgramAnalysis analysis)
    {
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
    /// Returns the locations a call to <paramref name="target"/> writes on every path that returns,
    /// directly or through a routine it calls. After such a call, the call is where a location's
    /// value came from, not just something that may have changed it.
    /// </summary>
    public ImmutableHashSet<Location> AlwaysWrittenBy(Symbol target) => Of(target).Always;

    /// <summary>
    /// Returns what a call to <paramref name="target"/> reads, may write and always writes, worked
    /// out the first time it is asked. A routine whose body is not in the program, and one that is
    /// already being worked out further up the calls, reads and writes nothing that can be seen.
    /// </summary>
    private Inferred Of(Symbol target)
    {
        var key = RoutineKey.Of(RegisterWalk.Owner(target) ?? target);
        if (found.TryGetValue(key, out var known))
            return known;
        if (!routines.TryGetValue(key, out var routine) || !active.Add(key))
            return Inferred.Nothing;
        var inferred = Infer(routine.Region, routine.File);
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
                var left = after.Locations.Union(into.Always);
                always = always is null ? left : always.Intersect(left);
            }
        }
        return new Inferred(reads.ToImmutable(), writes.ToImmutable(), always ?? []);

        Stored Through(BasicBlock block, Stored stored, ImmutableHashSet<Location>.Builder? read, ImmutableHashSet<Location>.Builder? written)
        {
            var locations = stored.Locations;
            foreach (var step in block.Steps)
            {
                if (MemoryAccess.Of(file, step) is not { } access)
                    continue;
                if (access.Reads && access.Direct is { } loaded && !locations.Contains(loaded))
                    read?.Add(loaded);
                foreach (var pointer in access.Pointer)
                {
                    if (!locations.Contains(pointer))
                        read?.Add(pointer);
                }
                if (!access.Stores)
                    continue;
                if (access.Direct is { } location)
                {
                    locations = locations.Add(location);
                    written?.Add(location);
                }
                else if (access.Indexed is { } start)
                {
                    written?.Add(start);
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
                    stores = stores is null ? called.Always : stores.Intersect(called.Always);
                }
                if (!block.CallsUnknown && stores is not null)
                    locations = locations.Union(stores);
            }
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
    private sealed record Inferred(
        ImmutableHashSet<Location> Reads, ImmutableHashSet<Location> Writes, ImmutableHashSet<Location> Always)
    {
        /// <summary>Gets what a routine nothing can be seen of reads and writes, which is nothing.</summary>
        public static Inferred Nothing { get; } = new([], [], []);
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
