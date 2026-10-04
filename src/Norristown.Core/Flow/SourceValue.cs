using System.Collections.Immutable;
using Norristown.Layout;

namespace Norristown.Flow;

/// <summary>
/// Represents where the value that one register or flag holds at a point was set, as the
/// <see cref="SourceWalk"/> follows it. It holds each <see cref="Origin"/> on a path that reaches
/// the point, and each through step, which is a step the value passed through unchanged. Two paths
/// that meet bring the union of both.
/// <para>
/// Each origin also carries the <see cref="RegisterValue"/> it put in the register, in the terms of
/// the <see cref="RegisterWalk"/>. That is what lets a test check that the two walks agree about
/// whether a register holds an entry value, a written value or an unknown one.
/// </para>
/// </summary>
internal sealed class SourceValue : IEquatable<SourceValue>
{
    private SourceValue(ImmutableDictionary<Origin, RegisterValue> origins, ImmutableHashSet<StepKey> through)
    {
        Origins = origins;
        Through = through;
    }

    /// <summary>Gets each origin of the value, with what it put in the register.</summary>
    public ImmutableDictionary<Origin, RegisterValue> Origins { get; }

    /// <summary>Gets each step the value passed through unchanged on its way here.</summary>
    public ImmutableHashSet<StepKey> Through { get; }

    /// <summary>Gets what the register may hold, in the terms of the <see cref="RegisterWalk"/>.</summary>
    public RegisterValue Value
    {
        get
        {
            var value = new RegisterValue(Processor.Registers.None, false, false);
            foreach (var held in Origins.Values)
                value = RegisterValue.Merge(value, held);
            return value;
        }
    }

    /// <summary>Returns a value set at one origin, with no through steps.</summary>
    public static SourceValue Of(Origin origin, RegisterValue value) =>
        new(ImmutableDictionary<Origin, RegisterValue>.Empty.Add(origin, value), []);

    /// <summary>Returns a value the analysis lost track of at <paramref name="blocker"/>.</summary>
    public static SourceValue Unknown(StepKey blocker) =>
        Of(new Origin(SourceKind.Unknown, blocker), RegisterValue.Unknown);

    /// <summary>
    /// Returns what either of two values may be, which is the union of their origins and of their
    /// through steps. Where both hold one origin, it puts in the register what either put there.
    /// </summary>
    public static SourceValue Merge(SourceValue a, SourceValue b)
    {
        if (ReferenceEquals(a, b) || a.Equals(b))
            return a;
        var origins = a.Origins;
        foreach (var (origin, value) in b.Origins)
            origins = origins.SetItem(origin, origins.TryGetValue(origin, out var held) ? RegisterValue.Merge(held, value) : value);
        return new SourceValue(origins, a.Through.Union(b.Through));
    }

    /// <summary>Returns this value with <paramref name="step"/> added to the steps it passed through.</summary>
    public SourceValue Via(StepKey step) => Through.Contains(step) ? this : new SourceValue(Origins, Through.Add(step));

    /// <inheritdoc/>
    public bool Equals(SourceValue? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (Origins.Count == other.Origins.Count && Through.SetEquals(other.Through)
                && Origins.All(pair => other.Origins.TryGetValue(pair.Key, out var value) && value == pair.Value)));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SourceValue);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // The hash has to be the same for two equal values whatever order their sets hold their
        // items in, so the items are combined in a way that does not depend on order.
        var hash = 0;
        foreach (var (origin, value) in Origins)
            hash ^= HashCode.Combine(origin, value);
        foreach (var step in Through)
            hash ^= step.GetHashCode() * 31;
        return hash;
    }
}
