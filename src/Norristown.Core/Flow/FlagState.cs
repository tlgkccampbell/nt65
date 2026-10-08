using System.Collections.Immutable;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents what is known about the N, Z, C, V, D and I flags at one point in a routine. A flag
/// is either known to be 0, known to be 1, or unknown. Only what the CPU defines is used, so
/// nothing about memory is assumed.
/// <para>
/// The state also records whether N and Z were last set from one result. A result with bit 7 set
/// is never zero, so where that holds, N known to be 1 means Z is 0, and Z known to be 1 means N
/// is 0.
/// </para>
/// <para>
/// Three more things are recorded for what calls leave behind. <see cref="Kept"/> holds the flags
/// that still hold what the routine was entered with, which is what the routine keeps where it
/// returns. <see cref="Unbacked"/> holds the flags whose value here a routine called on the way
/// gave without promising to, as <see cref="RoutineRegisters.Backed"/> does for registers.
/// <see cref="SourcesOf"/> names the routines whose answers each flag depends on, so that a
/// decision taken on one can be taken again when that answer changes.
/// </para>
/// </summary>
internal sealed class FlagState : IEquatable<FlagState>
{
    /// <summary>The flags the analysis follows.</summary>
    public const StatusFlags Followed = StatusFlags.Negative | StatusFlags.Zero | StatusFlags.Carry | StatusFlags.Overflow
        | StatusFlags.Decimal | StatusFlags.InterruptDisable;

    private static readonly ImmutableDictionary<StatusFlags, ImmutableHashSet<Symbol>> NoSources =
        ImmutableDictionary<StatusFlags, ImmutableHashSet<Symbol>>.Empty;

    private static readonly ImmutableDictionary<StatusFlags, Symbol> NoOrigins = ImmutableDictionary<StatusFlags, Symbol>.Empty;

    // The routines whose answers each flag depends on, and, for each unbacked flag, the routine
    // that declined to promise it.
    private readonly ImmutableDictionary<StatusFlags, ImmutableHashSet<Symbol>> sources;
    private readonly ImmutableDictionary<StatusFlags, Symbol> origins;

    private FlagState(
        StatusFlags known, StatusFlags set, bool shared, StatusFlags kept = StatusFlags.None,
        StatusFlags unbacked = StatusFlags.None, StatusFlags quiet = StatusFlags.None,
        ImmutableDictionary<StatusFlags, ImmutableHashSet<Symbol>>? sources = null,
        ImmutableDictionary<StatusFlags, Symbol>? origins = null)
    {
        Known = known & Followed;
        Set = set & Known;
        Shared = shared;
        Kept = kept & Followed;
        Unbacked = unbacked & Followed;
        Quiet = quiet & Followed & ~Unbacked;
        this.sources = sources ?? NoSources;
        this.origins = origins ?? NoOrigins;
    }

    /// <summary>Gets the state in which nothing is known about any flag.</summary>
    public static FlagState Unknown { get; } = new(StatusFlags.None, StatusFlags.None, false);

    /// <summary>Gets the flags whose values are known.</summary>
    public StatusFlags Known { get; }

    /// <summary>Gets the known flags that are 1. Every other known flag is 0.</summary>
    public StatusFlags Set { get; }

    /// <summary>Gets a value indicating whether N and Z were last set from one result.</summary>
    public bool Shared { get; }

    /// <summary>Gets the flags that hold, on every path, what the routine was entered with.</summary>
    public StatusFlags Kept { get; }

    /// <summary>
    /// Gets the flags whose value here, or for D and I whose being kept, relies on a routine
    /// called on the way that did not promise it. Relying on one is reported.
    /// </summary>
    public StatusFlags Unbacked { get; }

    /// <summary>
    /// Gets the flags whose value here relies on a routine called directly from this one keeping
    /// it without promising to. The register walk reports relying on such a keep, so this state
    /// does not, but a routine whose exit value comes from one does not promise it either.
    /// </summary>
    public StatusFlags Quiet { get; }

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
        StatusFlags.Decimal => "D",
        StatusFlags.InterruptDisable => "I",
        _ => flag.ToString(),
    };

    /// <summary>
    /// Returns the state in which the flags <paramref name="values"/> gives have those values and
    /// nothing else is known, as at a label entered from outside the routine.
    /// </summary>
    public static FlagState Given(FlagValues values) =>
        values.Known == StatusFlags.None ? Unknown : new(values.Known, values.Set, false);

    /// <summary>
    /// Returns the state at a routine's entry, where every flag holds what the caller left and
    /// the flags <paramref name="values"/> gives have those values.
    /// </summary>
    public static FlagState Entered(FlagValues values) => new(values.Known, values.Set, false, Followed);

    /// <summary>
    /// Returns the routines whose answers what is known about <paramref name="flag"/> here depends
    /// on.
    /// </summary>
    public IEnumerable<Symbol> SourcesOf(StatusFlags flag) => sources.GetValueOrDefault(flag) ?? [];

    /// <summary>
    /// Returns the routine that declined to promise what <paramref name="flag"/> holds here, for a
    /// flag in <see cref="Unbacked"/> or <see cref="Quiet"/>, or null.
    /// </summary>
    public Symbol? OriginOf(StatusFlags flag) => origins.GetValueOrDefault(flag);

    /// <summary>
    /// Returns the state after a call to <paramref name="callee"/>, which returns with
    /// <paramref name="returned"/>. The flags it keeps are as they were before the call and the
    /// ones it gives values have them. Every other flag is unknown. N and Z stay related only
    /// where both are kept. With <paramref name="track"/>, every flag depends on the callee's answer
    /// from here on; without it, as when a routine's own answer is being worked out, which
    /// answers each flag depends on is not recorded.
    /// </summary>
    public FlagState Returned(Symbol callee, RoutineFlags returned, bool track = true)
    {
        var values = returned.Values;
        var through = returned.Kept & ~values.Known;
        var known = (Known & through) | values.Known;
        var set = (Set & through) | values.Set;
        var nz = StatusFlags.Negative | StatusFlags.Zero;

        // A flag's value that came before the call is unbacked where the callee keeps it without
        // promising to. One the callee gives is unbacked where it gives it without promising to.
        var unbacked = (Unbacked & through) | (returned.Unbacked & (values.Known | (through & (Known | Kept))));
        var quiet = (Quiet & through) | (returned.QuietlyKept & through & Known);
        var origins = this.origins;
        foreach (var flag in FlagValues.Named)
        {
            if (((unbacked | quiet) & flag) != 0 && ((returned.Unbacked | returned.QuietlyKept) & flag) != 0)
                origins = origins.SetItem(flag, returned.DeclinerOf(flag) ?? callee);
            else if ((through & flag) == 0)
                origins = origins.Remove(flag);
        }
        var sources = NoSources;
        if (track)
        {
            var builder = NoSources.ToBuilder();
            foreach (var flag in FlagValues.Named)
            {
                var before = (through & flag) != 0 ? this.sources.GetValueOrDefault(flag) : null;
                builder[flag] = (before ?? []).Add(callee);
            }
            sources = builder.ToImmutable();
        }
        return new(known, set, Shared && (through & nz) == nz, Kept & through, unbacked, quiet, sources, origins);
    }

    /// <summary>
    /// Returns whether what is known about <paramref name="flag"/> here comes from this routine's
    /// own code and its signature alone, and not from what a routine it calls returns with. A hint
    /// goes only by such a flag, so that it neither relies on another routine's body nor goes
    /// stale when that body changes.
    /// </summary>
    public bool IsFirm(StatusFlags flag) => !sources.ContainsKey(flag) && IsBacked(flag);

    /// <summary>
    /// Returns whether what is known about <paramref name="flag"/> here relies on nothing a
    /// routine called on the way declined to promise.
    /// </summary>
    public bool IsBacked(StatusFlags flag) => ((Unbacked | Quiet) & flag) == 0;

    /// <summary>
    /// Returns the value of <paramref name="flag"/>, or null where it is not known.
    /// </summary>
    public bool? ValueOf(StatusFlags flag) => (Known & flag) == 0 ? null : (Set & flag) != 0;

    /// <summary>
    /// Returns the state after an instruction sets <paramref name="flag"/> to
    /// <paramref name="value"/>, which says nothing about how N and Z relate.
    /// </summary>
    public FlagState With(StatusFlags flag, bool value) =>
        new(Known | flag, value ? Set | flag : Set & ~flag, Shared && (flag & (StatusFlags.Negative | StatusFlags.Zero)) == 0,
            Kept & ~flag, Unbacked & ~flag, Quiet & ~flag, Without(sources, flag), Without(origins, flag));

    /// <summary>
    /// Returns the state after an instruction sets N and Z from one result whose value is
    /// <paramref name="value"/>, as an immediate load does. <paramref name="bits"/> is the width
    /// of the result, 8 or 16.
    /// </summary>
    public FlagState Loaded(long value, int bits)
    {
        var nz = StatusFlags.Negative | StatusFlags.Zero;
        var mask = (1L << bits) - 1;
        var negative = (value & (1L << (bits - 1))) != 0;
        var zero = (value & mask) == 0;
        var set = Set & ~nz;
        if (negative)
            set |= StatusFlags.Negative;
        if (zero)
            set |= StatusFlags.Zero;
        return new(Known | nz, set, true, Kept & ~nz, Unbacked & ~nz, Quiet & ~nz, Without(sources, nz), Without(origins, nz));
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
        return written == StatusFlags.None
            ? this
            : new(Known & ~written, Set, keepsShared, Kept & ~written, Unbacked & ~written, Quiet & ~written,
                Without(sources, written), Without(origins, written));
    }

    /// <summary>
    /// Returns the state on a path where a branch found <paramref name="flag"/> to be
    /// <paramref name="value"/>. Where N and Z came from one result, learning one of them can
    /// say what the other is. A value learned this way relies on nothing a routine promised.
    /// </summary>
    public FlagState Learn(StatusFlags flag, bool value)
    {
        var learned = flag;
        var set = value ? Set | flag : Set & ~flag;
        if (Shared && value && flag is StatusFlags.Negative or StatusFlags.Zero)
        {
            var other = flag == StatusFlags.Negative ? StatusFlags.Zero : StatusFlags.Negative;
            learned |= other;
            set &= ~other;
        }
        return new(Known | learned, set, Shared, Kept, Unbacked & ~learned, Quiet & ~learned,
            Without(sources, learned), Without(origins, learned));
    }

    /// <summary>
    /// Returns what is known where this state and <paramref name="other"/> meet. A flag stays
    /// known only where both agree on its value, and kept only where both keep it. N and Z stay
    /// related where the relation holds on both paths, either because they came from one result
    /// or because one of them is 0. What either path relies on, the meeting relies on.
    /// </summary>
    public FlagState Merge(FlagState other)
    {
        var known = Known & other.Known & ~(Set ^ other.Set);
        var sources = this.sources;
        foreach (var (flag, from) in other.sources)
            sources = sources.SetItem(flag, sources.TryGetValue(flag, out var mine) ? mine.Union(from) : from);
        var origins = this.origins;
        foreach (var (flag, origin) in other.origins)
        {
            if (!origins.ContainsKey(flag))
                origins = origins.Add(flag, origin);
        }
        return new(known, Set & known, Related() && other.Related(), Kept & other.Kept,
            Unbacked | other.Unbacked, Quiet | other.Quiet, sources, origins);
    }

    /// <inheritdoc/>
    public bool Equals(FlagState? other) =>
        other is not null && Known == other.Known && Set == other.Set && Shared == other.Shared && Kept == other.Kept
        && Unbacked == other.Unbacked && Quiet == other.Quiet && SameSources(other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as FlagState);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Known, Set, Shared, Kept, Unbacked, Quiet, sources.Count);

    /// <summary>Returns <paramref name="map"/> without the entries for <paramref name="flags"/>.</summary>
    private static ImmutableDictionary<StatusFlags, T> Without<T>(ImmutableDictionary<StatusFlags, T> map, StatusFlags flags)
    {
        if (map.IsEmpty)
            return map;
        foreach (var flag in FlagValues.Named)
        {
            if ((flags & flag) != 0)
                map = map.Remove(flag);
        }
        return map;
    }

    /// <summary>Returns whether this state and <paramref name="other"/> name the same sources for every flag.</summary>
    private bool SameSources(FlagState other)
    {
        if (ReferenceEquals(sources, other.sources))
            return true;
        if (sources.Count != other.sources.Count)
            return false;
        foreach (var (flag, from) in sources)
        {
            if (!other.sources.TryGetValue(flag, out var theirs) || !from.SetEquals(theirs))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns whether N known to be 1 means Z is 0 here, either because the two came from one
    /// result or because one of them is already known to be 0.
    /// </summary>
    private bool Related() =>
        Shared || ValueOf(StatusFlags.Negative) == false || ValueOf(StatusFlags.Zero) == false;
}
