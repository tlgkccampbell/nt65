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
/// of the question. It is a best guess for showing. Nothing that warns or errors depends on it, and
/// a routine that calls itself, directly or not, is taken to read nothing more through that call.
/// </para>
/// </summary>
internal sealed class MemoryInference
{
    private readonly Dictionary<RoutineKey, (FlowRegion Region, FileAnalysis File)> routines = [];
    private readonly Dictionary<RoutineKey, (ImmutableHashSet<Location> Reads, ImmutableHashSet<Location> Writes)> found = [];
    private readonly HashSet<RoutineKey> active = [];

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
    /// Returns what a call to <paramref name="target"/> reads and may write, worked out the first
    /// time it is asked. A routine whose body is not in the program, and one that is already being
    /// worked out further up the calls, reads and writes nothing that can be seen.
    /// </summary>
    private (ImmutableHashSet<Location> Reads, ImmutableHashSet<Location> Writes) Of(Symbol target)
    {
        var key = RoutineKey.Of(RegisterWalk.Owner(target) ?? target);
        if (found.TryGetValue(key, out var known))
            return known;
        if (!routines.TryGetValue(key, out var routine) || !active.Add(key))
            return ([], []);
        var inferred = Infer(routine.Region, routine.File);
        active.Remove(key);
        found[key] = inferred;
        return inferred;
    }

    /// <summary>
    /// Returns what one routine reads before writing, and what it may write. The blocks are run to a
    /// fixed point over the locations every path has stored to, and then walked once more to collect
    /// the reads, because a read counts only against what is stored on every path to it.
    /// </summary>
    private (ImmutableHashSet<Location> Reads, ImmutableHashSet<Location> Writes) Infer(FlowRegion region, FileAnalysis file)
    {
        var blocks = region.Blocks;
        var reads = ImmutableHashSet.CreateBuilder<Location>();
        var writes = ImmutableHashSet.CreateBuilder<Location>();
        var solver = new Dataflow<Stored>(
            blocks, (block, stored) => Through(block, stored, null, null), Stored.Merge, block => ControlFlow.Onward(blocks, block));
        if (blocks.Count == 0)
            return ([], []);
        solver.Enter(0, Stored.Nothing);
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is { } stored)
                Through(block, stored, reads, writes);
        }
        return (reads.ToImmutable(), writes.ToImmutable());

        Stored Through(BasicBlock block, Stored stored, ImmutableHashSet<Location>.Builder? read, ImmutableHashSet<Location>.Builder? written)
        {
            var locations = stored.Locations;
            foreach (var step in block.Steps)
            {
                if (MemoryAccess.Of(file.Model, file.Layout, step) is not { } access)
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
                foreach (var callee in block.Calls)
                {
                    var (calleeReads, calleeWrites) = Of(callee);
                    foreach (var location in calleeReads)
                    {
                        if (!locations.Contains(location))
                            read?.Add(location);
                    }
                    written?.UnionWith(calleeWrites);
                }
            }
            return new Stored(locations);
        }
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
