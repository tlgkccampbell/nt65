using System.Collections.Immutable;
using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents where the value of each register, flag and width was set at one point in a routine,
/// and what the routine has pushed, as the <see cref="SourceWalk"/> follows them.
/// </summary>
internal sealed class SourceState : IEquatable<SourceState>
{
    private static readonly int Count = Enum.GetValues<Tracked>().Length;

    private readonly ImmutableArray<SourceValue> values;

    private SourceState(
        ImmutableArray<SourceValue> values, SourceStack? stack, Registers pointing = Registers.None, SourceStack? pointed = null)
    {
        this.values = values;
        Stack = stack;
        Pointing = pointing;
        Pointed = pointed;
    }

    /// <summary>
    /// Gets the flags and widths a <c>php</c> saves and a <c>plp</c> restores, in the order a
    /// <see cref="SourcePush"/> holds them.
    /// </summary>
    public static ImmutableArray<Tracked> Flags { get; } =
        [Tracked.C, Tracked.N, Tracked.Z, Tracked.V, Tracked.M, Tracked.Index];

    /// <summary>
    /// Gets the state of a routine when it is entered, in which the caller set every value and
    /// nothing is pushed.
    /// </summary>
    public static SourceState Entered { get; } = new(
        [.. Enum.GetValues<Tracked>().Select(tracked => SourceValue.Of(Origin.Entry, EntryValue(tracked)))],
        SourceStack.Empty);

    /// <summary>Gets what the routine has pushed, or null where that is not known.</summary>
    public SourceStack? Stack { get; }

    /// <summary>
    /// Gets the registers among A, X and Y that hold the stack pointer, as X does after
    /// <c>tsx</c>.
    /// </summary>
    public Registers Pointing { get; }

    /// <summary>
    /// Gets the stack as it was where the registers in <see cref="Pointing"/> copied the stack
    /// pointer, or null where that cannot be relied on. <see cref="StackPointerCopies"/> says when
    /// a <c>txs</c> or <c>tcs</c> moves the stack back to it.
    /// </summary>
    public SourceStack? Pointed { get; }

    /// <summary>
    /// Returns the state at a point the analysis lost track of every value at, such as a label that
    /// control reaches only from outside the routine. <paramref name="blocker"/> is the step it
    /// lost track at.
    /// </summary>
    public static SourceState Unknown(Layout.StepKey blocker, SourceStack? stack) =>
        new([.. Enumerable.Repeat(SourceValue.Unknown(blocker), Count)], stack);

    /// <summary>
    /// Returns what two paths arriving at one place bring, which is everything either brings. A stack
    /// the two disagree about is unknown.
    /// </summary>
    public static SourceState Merge(SourceState? known, SourceState arriving)
    {
        if (known is null)
            return arriving;
        if (known.Equals(arriving))
            return known;
        var merged = ImmutableArray.CreateBuilder<SourceValue>(Count);
        for (var i = 0; i < Count; i++)
            merged.Add(SourceValue.Merge(known.values[i], arriving.values[i]));
        return new SourceState(
            merged.MoveToImmutable(), SourceStack.Merge(known.Stack, arriving.Stack), known.Pointing & arriving.Pointing,
            Equals(known.Pointed, arriving.Pointed) ? known.Pointed : null);
    }

    /// <summary>
    /// Returns what the <see cref="RegisterWalk"/> counts as the entry value of
    /// <paramref name="tracked"/>. Only the registers it follows have one, and the high byte of the
    /// accumulator holds part of the accumulator's.
    /// </summary>
    public static RegisterValue EntryValue(Tracked tracked) => tracked switch
    {
        Tracked.A or Tracked.AHigh => RegisterValue.Of(Registers.A),
        Tracked.X => RegisterValue.Of(Registers.X),
        Tracked.Y => RegisterValue.Of(Registers.Y),
        Tracked.C => RegisterValue.Of(Registers.C),
        Tracked.Z => RegisterValue.Of(Registers.Z),
        Tracked.N => RegisterValue.Of(Registers.N),
        Tracked.V => RegisterValue.Of(Registers.V),
        _ => new RegisterValue(Registers.None, false, false),
    };

    /// <summary>Returns which of the followed values <paramref name="register"/> is.</summary>
    public static Tracked Track(Registers register) => register switch
    {
        Registers.A => Tracked.A,
        Registers.X => Tracked.X,
        Registers.Y => Tracked.Y,
        Registers.C => Tracked.C,
        Registers.Z => Tracked.Z,
        Registers.N => Tracked.N,
        _ => Tracked.V,
    };

    /// <summary>Returns where the value of <paramref name="tracked"/> was set.</summary>
    public SourceValue Of(Tracked tracked) => values[(int)tracked];

    /// <summary>
    /// Returns where the whole of <paramref name="register"/> was set. For the accumulator that is
    /// where either of its halves was.
    /// </summary>
    public SourceValue Whole(Registers register) =>
        register == Registers.A ? SourceValue.Merge(Of(Tracked.A), Of(Tracked.AHigh)) : Of(Track(register));

    /// <summary>
    /// Returns this state with <paramref name="tracked"/> set where <paramref name="value"/> says.
    /// </summary>
    public SourceState With(Tracked tracked, SourceValue value) =>
        ReferenceEquals(values[(int)tracked], value) ? this : new(values.SetItem((int)tracked, value), Stack, Pointing, Pointed);

    /// <summary>
    /// Returns this state with <paramref name="register"/> set where <paramref name="value"/> says.
    /// For the accumulator, both of its halves are set.
    /// </summary>
    public SourceState With(Registers register, SourceValue value) =>
        register == Registers.A
            ? With(Tracked.A, value).With(Tracked.AHigh, value)
            : With(Track(register), value);

    /// <summary>Returns this state with <paramref name="stack"/> as what the routine has pushed.</summary>
    public SourceState WithStack(SourceStack? stack) => ReferenceEquals(Stack, stack) ? this : new(values, stack, Pointing, Pointed);

    /// <summary>
    /// Returns this state with <paramref name="pointing"/> as the registers that hold the stack
    /// pointer, and <paramref name="pointed"/> as the stack their copy was taken from.
    /// </summary>
    public SourceState WithCopy(Registers pointing, SourceStack? pointed) =>
        Pointing == pointing && ReferenceEquals(Pointed, pointed) ? this : new(values, Stack, pointing, pointed);

    /// <inheritdoc/>
    public bool Equals(SourceState? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (values.AsSpan().SequenceEqual(other.values.AsSpan()) && Equals(Stack, other.Stack)
                && Pointing == other.Pointing && Equals(Pointed, other.Pointed)));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SourceState);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in values)
            hash.Add(value);
        hash.Add(Stack);
        hash.Add(Pointing);
        hash.Add(Pointed);
        return hash.ToHashCode();
    }
}
