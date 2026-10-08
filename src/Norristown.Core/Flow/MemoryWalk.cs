using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Follows where the values of some locations in memory were set, through one routine, for the
/// memory part of <see cref="InputSources"/>. The source of a location's value is the last direct
/// store to it on each path, or the routine's caller where no store reaches.
/// <para>
/// Memory is the programmer's to vouch for, so this is a best guess and feeds no check. What might
/// also have changed a value does not stop the search, but is kept as a doubt to name in the hover.
/// A doubt is an indexed store from the same symbol, any indirect store, a call that may write the
/// location, or a store to another symbol at the same address.
/// </para>
/// </summary>
internal sealed class MemoryWalk
{
    private readonly FileAnalysis file;
    private readonly MemoryInference inference;
    private readonly ImmutableArray<Location> wanted;

    /// <summary>
    /// Initializes a walk over <paramref name="file"/>'s routines that follows
    /// <paramref name="wanted"/>, with <paramref name="inference"/> saying what each routine called
    /// may write.
    /// </summary>
    public MemoryWalk(FileAnalysis file, MemoryInference inference, IEnumerable<Location> wanted)
    {
        this.file = file;
        this.inference = inference;
        this.wanted = [.. wanted.Distinct()];
    }

    /// <summary>
    /// Returns what reaches each block of <paramref name="region"/> from the routine's entry, or null
    /// for a block nothing reaches.
    /// </summary>
    public State?[] Solve(FlowRegion region)
    {
        var blocks = region.Blocks;
        var solver = new Dataflow<State>(blocks, Through, State.Merge, block => ControlFlow.Onward(blocks, block));
        if (blocks.Count > 0)
            solver.Enter(0, new State(wanted.ToImmutableDictionary(location => location, _ => Value.Entered)));
        return solver.Reached;
    }

    /// <summary>
    /// Returns where each location's value was set just before the step at <paramref name="index"/>
    /// in <paramref name="block"/>, given the state that reaches the block.
    /// </summary>
    public State Before(BasicBlock block, State reached, int index)
    {
        var state = reached;
        for (var i = 0; i < index; i++)
            state = After(block, i, state);
        return state;
    }

    /// <summary>
    /// Returns what a store leaves for one followed location. A direct store to the location is its
    /// new source, and that includes a 16-bit store to the byte before it. Any other store that might
    /// reach it is a doubt.
    /// </summary>
    private static Value Stored(Location location, Value value, MemoryAccess access, StepKey step)
    {
        if (access.Direct is not null)
        {
            var bytes = access.DirectBytes;
            if (bytes.Contains(location))
                return Value.Set(step);
            return bytes.Any(direct => Overlaps(direct, location)) ? value.Doubted(step) : value;
        }
        if (access.Indirect || (access.Indexed is { } start && start.Group == location.Group))
            return value.Doubted(step);
        return value;
    }

    /// <summary>
    /// Returns whether two different names may stand for the same byte. They do where the source
    /// fixes both addresses and they are equal.
    /// </summary>
    private static bool Overlaps(Location a, Location b) =>
        a != b && a.Address is { } at && at == b.Address;

    /// <summary>Returns where each location's value was set after one block, from the state that reaches it.</summary>
    private State Through(BasicBlock block, State state)
    {
        for (var i = 0; i < block.Steps.Count; i++)
            state = After(block, i, state);
        return state;
    }

    /// <summary>
    /// Returns where each location's value was set after the step at <paramref name="index"/> in
    /// <paramref name="block"/>. The calls a block ends with take effect after its last step.
    /// </summary>
    private State After(BasicBlock block, int index, State state)
    {
        var step = block.Steps[index];
        if (step.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Brk or MnemonicKind.Cop })
            state = state.Each((_, value) => value.Doubted(step.Key));
        else if (MemoryAccess.Of(file, step) is { Stores: true } access)
            state = state.Each((location, value) => Stored(location, value, access, step.Key));

        if (index == block.Steps.Count - 1 && RegisterWalk.CallsAtEnd(block))
        {
            // A call that writes a location on every path is where its value came from. One that
            // only may write it, or that nt65 cannot follow, is a doubt.
            var written = block.CallsUnknown ? null : block.Calls.SelectMany(inference.WritesOf).ToHashSet();
            var always = block.CallsUnknown || block.Calls.Count == 0 ? null
                : block.Calls.Select(inference.AlwaysWrittenBy).Aggregate((a, b) => a.Intersect(b));
            state = state.Each((location, value) =>
                always is not null && always.Contains(location) ? Value.Called(step.Key)
                : written is null || written.Contains(location) || written.Any(write => Overlaps(write, location))
                    ? value.Doubted(step.Key)
                    : value);
        }
        return state;
    }

    /// <summary>Represents where each followed location's value was set at one point in a routine.</summary>
    internal sealed class State(ImmutableDictionary<Location, Value> values) : IEquatable<State>
    {
        /// <summary>Gets where each followed location's value was set.</summary>
        public ImmutableDictionary<Location, Value> Values { get; } = values;

        /// <summary>Returns what two paths that meet bring, which is everything either brings.</summary>
        public static State Merge(State? known, State arriving)
        {
            if (known is null)
                return arriving;
            if (known.Equals(arriving))
                return known;
            return new State(known.Values.ToImmutableDictionary(
                pair => pair.Key, pair => Value.Merge(pair.Value, arriving.Values[pair.Key])));
        }

        /// <summary>Returns this state with each location's value replaced by what <paramref name="change"/> makes of it.</summary>
        public State Each(Func<Location, Value, Value> change)
        {
            var changed = Values;
            foreach (var (location, value) in Values)
            {
                var after = change(location, value);
                if (!ReferenceEquals(after, value))
                    changed = changed.SetItem(location, after);
            }
            return ReferenceEquals(changed, Values) ? this : new State(changed);
        }

        /// <inheritdoc/>
        public bool Equals(State? other) =>
            other is not null && Values.Count == other.Values.Count
            && Values.All(pair => other.Values.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value));

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as State);

        /// <inheritdoc/>
        public override int GetHashCode() => Values.Count;
    }

    /// <summary>
    /// Represents where one location's value was set, as a set of sources, and what else might have
    /// changed it since.
    /// </summary>
    internal sealed class Value(ImmutableHashSet<Origin> origins, ImmutableHashSet<StepKey> doubts) : IEquatable<Value>
    {
        /// <summary>Gets the value of a location the routine's caller set.</summary>
        public static Value Entered { get; } = new([Origin.Entry], []);

        /// <summary>Gets each place the value was set.</summary>
        public ImmutableHashSet<Origin> Origins { get; } = origins;

        /// <summary>Gets each step since then that might also have changed it.</summary>
        public ImmutableHashSet<StepKey> Doubts { get; } = doubts;

        /// <summary>Returns the value a direct store at <paramref name="step"/> left.</summary>
        public static Value Set(StepKey step) => new([new Origin(SourceKind.Instruction, step)], []);

        /// <summary>Returns the value a call at <paramref name="step"/> left, whose routine always writes the location.</summary>
        public static Value Called(StepKey step) => new([new Origin(SourceKind.Call, step)], []);

        /// <summary>Returns what either of two values may be.</summary>
        public static Value Merge(Value a, Value b) =>
            a.Equals(b) ? a : new Value(a.Origins.Union(b.Origins), a.Doubts.Union(b.Doubts));

        /// <summary>Returns this value with <paramref name="step"/> as something that might have changed it.</summary>
        public Value Doubted(StepKey step) => Doubts.Contains(step) ? this : new Value(Origins, Doubts.Add(step));

        /// <inheritdoc/>
        public bool Equals(Value? other) =>
            other is not null && Origins.SetEquals(other.Origins) && Doubts.SetEquals(other.Doubts);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as Value);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Origins.Count, Doubts.Count);
    }
}
