using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what is known about the N, Z, C and V flags at one point in a routine. A flag is
/// either known to be 0, known to be 1, or unknown. Only what the CPU defines is used, so nothing
/// about memory is assumed.
/// <para>
/// The state also records whether N and Z were last set from one result. A result with bit 7 set
/// is never zero, so where that holds, N known to be 1 means Z is 0, and Z known to be 1 means N
/// is 0.
/// </para>
/// </summary>
internal sealed class FlagState : IEquatable<FlagState>
{
    /// <summary>The flags the analysis follows.</summary>
    public const StatusFlags Followed = StatusFlags.Negative | StatusFlags.Zero | StatusFlags.Carry | StatusFlags.Overflow;

    private FlagState(StatusFlags known, StatusFlags set, bool shared)
    {
        Known = known & Followed;
        Set = set & Known;
        Shared = shared;
    }

    /// <summary>Gets the state in which nothing is known about any flag.</summary>
    public static FlagState Unknown { get; } = new(StatusFlags.None, StatusFlags.None, false);

    /// <summary>Gets the flags whose values are known.</summary>
    public StatusFlags Known { get; }

    /// <summary>Gets the known flags that are 1. Every other known flag is 0.</summary>
    public StatusFlags Set { get; }

    /// <summary>Gets a value indicating whether N and Z were last set from one result.</summary>
    public bool Shared { get; }

    /// <summary>
    /// Returns the words a message uses to say that <paramref name="flag"/> is
    /// <paramref name="value"/> here, such as "C is 1 here".
    /// </summary>
    public static string Describe(StatusFlags flag, bool value) => $"{Name(flag)} is {(value ? 1 : 0)} here";

    /// <summary>Returns the letter that names one flag.</summary>
    public static string Name(StatusFlags flag) => flag switch
    {
        StatusFlags.Negative => "N",
        StatusFlags.Zero => "Z",
        StatusFlags.Carry => "C",
        StatusFlags.Overflow => "V",
        _ => flag.ToString(),
    };

    /// <summary>
    /// Returns the value of <paramref name="flag"/>, or null where it is not known.
    /// </summary>
    public bool? ValueOf(StatusFlags flag) => (Known & flag) == 0 ? null : (Set & flag) != 0;

    /// <summary>
    /// Returns the state after an instruction sets <paramref name="flag"/> to
    /// <paramref name="value"/>, which says nothing about how N and Z relate.
    /// </summary>
    public FlagState With(StatusFlags flag, bool value) =>
        new(Known | flag, value ? Set | flag : Set & ~flag, Shared && (flag & (StatusFlags.Negative | StatusFlags.Zero)) == 0);

    /// <summary>
    /// Returns the state after an instruction sets N and Z from one result whose value is
    /// <paramref name="value"/>, as an immediate load does. <paramref name="bits"/> is the width
    /// of the result, 8 or 16.
    /// </summary>
    public FlagState Loaded(long value, int bits)
    {
        var mask = (1L << bits) - 1;
        var negative = (value & (1L << (bits - 1))) != 0;
        var zero = (value & mask) == 0;
        var set = Set & ~(StatusFlags.Negative | StatusFlags.Zero);
        if (negative)
            set |= StatusFlags.Negative;
        if (zero)
            set |= StatusFlags.Zero;
        return new(Known | StatusFlags.Negative | StatusFlags.Zero, set, true);
    }

    /// <summary>
    /// Returns the state after an instruction writes the <paramref name="written"/> flags with
    /// values nothing here can know. <paramref name="shared"/> says whether it sets N and Z from
    /// one result.
    /// </summary>
    public FlagState Forget(StatusFlags written, bool shared)
    {
        var nz = StatusFlags.Negative | StatusFlags.Zero;
        var keepsShared = (written & nz) == 0 ? Shared : shared && (written & nz) == nz;
        return written == StatusFlags.None ? this : new(Known & ~written, Set, keepsShared);
    }

    /// <summary>
    /// Returns the state on a path where a branch found <paramref name="flag"/> to be
    /// <paramref name="value"/>. Where N and Z came from one result, learning one of them can
    /// say what the other is.
    /// </summary>
    public FlagState Learn(StatusFlags flag, bool value)
    {
        var learned = new FlagState(Known | flag, value ? Set | flag : Set & ~flag, Shared);
        if (!Shared)
            return learned;
        if (flag == StatusFlags.Negative && value)
            return new(learned.Known | StatusFlags.Zero, learned.Set & ~StatusFlags.Zero, true);
        if (flag == StatusFlags.Zero && value)
            return new(learned.Known | StatusFlags.Negative, learned.Set & ~StatusFlags.Negative, true);
        return learned;
    }

    /// <summary>
    /// Returns what is known where this state and <paramref name="other"/> meet. A flag stays
    /// known only where both agree on its value. N and Z stay related where the relation holds
    /// on both paths, either because they came from one result or because one of them is 0.
    /// </summary>
    public FlagState Merge(FlagState other)
    {
        var known = Known & other.Known & ~(Set ^ other.Set);
        return new(known, Set & known, Related() && other.Related());
    }

    /// <inheritdoc/>
    public bool Equals(FlagState? other) =>
        other is not null && Known == other.Known && Set == other.Set && Shared == other.Shared;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as FlagState);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Known, Set, Shared);

    /// <summary>
    /// Returns whether N known to be 1 means Z is 0 here, either because the two came from one
    /// result or because one of them is already known to be 0.
    /// </summary>
    private bool Related() =>
        Shared || ValueOf(StatusFlags.Negative) == false || ValueOf(StatusFlags.Zero) == false;
}
