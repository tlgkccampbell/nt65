using System.Collections.Immutable;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents what a call to a routine returns with in the flags: the flags it hands back as it
/// found them, and the values it gives others. It comes from the routine's signature where the
/// routine has no body, and otherwise from its body, as <see cref="FlagExits"/> works it out.
/// </summary>
/// <param name="Kept">The flags the routine returns as its caller left them.</param>
/// <param name="Values">The values the routine returns some flags with.</param>
/// <param name="Unbacked">
/// The flags among <paramref name="Values"/>, and among D and I in <paramref name="Kept"/>,
/// that the routine returns without promising to, or only because a routine it calls does.
/// See <see cref="RoutineRegisters.Backed"/>.
/// </param>
/// <param name="QuietlyKept">
/// The flags among C, Z, N and V in <paramref name="Kept"/> that the routine keeps without
/// promising to. The register walk reports relying on such a keep.
/// </param>
public readonly record struct RoutineFlags(StatusFlags Kept, FlagValues Values, StatusFlags Unbacked, StatusFlags QuietlyKept)
{
    /// <summary>Gets what a routine nothing is known about returns with, which is no flag known.</summary>
    public static RoutineFlags Nothing => new(StatusFlags.None, FlagValues.None, StatusFlags.None, StatusFlags.None);

    /// <summary>
    /// Gets what is assumed of a routine not yet worked out, which keeps every flag. Working the
    /// routines out only takes from this.
    /// </summary>
    public static RoutineFlags Everything => new(FlagState.Followed, FlagValues.None, StatusFlags.None, StatusFlags.None);

    /// <summary>
    /// Gets the routine that declined to promise each flag in <see cref="Unbacked"/> or
    /// <see cref="QuietlyKept"/>, where that is a routine it calls.
    /// </summary>
    public ImmutableDictionary<StatusFlags, Symbol>? Decliners { get; init; }

    /// <summary>
    /// Returns the routine that declined to promise <paramref name="flag"/>, or null where that
    /// is the routine itself.
    /// </summary>
    public Symbol? DeclinerOf(StatusFlags flag) => Decliners?.GetValueOrDefault(flag);

    /// <summary>
    /// Returns what a caller that tests <paramref name="flag"/> after the call learns from this
    /// answer: whether the flag is kept, its value, and whether that is promised.
    /// </summary>
    public (bool Kept, bool? Value, bool Unbacked, bool Quiet) For(StatusFlags flag) =>
        ((Kept & flag) != 0, Values.ValueOf(flag), (Unbacked & flag) != 0, (QuietlyKept & flag) != 0);

    /// <summary>Returns whether two answers say the same about every flag. Which routines declined is not compared.</summary>
    public bool Equals(RoutineFlags other) =>
        Kept == other.Kept && Values == other.Values && Unbacked == other.Unbacked && QuietlyKept == other.QuietlyKept;

    /// <summary>Returns a hash code over the same parts that <see cref="Equals(RoutineFlags)"/> compares.</summary>
    public override int GetHashCode() => HashCode.Combine(Kept, Values, Unbacked, QuietlyKept);
}
