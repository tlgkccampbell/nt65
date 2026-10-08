using System.Collections.Immutable;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Holds the <see cref="RoutineFlags"/> of every routine of the program, which is what a call to
/// it returns with in the flags. A routine with a body is worked out from that body: the flags it
/// returns as its caller left them, and the values it returns others with on every path. A routine
/// that names any flag after <c>-&gt;</c> promises only what it names, and what else it is found
/// to return is unbacked, as a <c>keeps</c> beyond what is declared is. A routine with no body
/// returns with what its signature says.
/// <para>
/// The flag analysis of each file takes these answers for the calls it follows, and records the
/// ones a decision depended on. A file that took an answer the program turns out to differ on is
/// analyzed again, as one that took a stale <see cref="StackEffects"/> is.
/// </para>
/// </summary>
public sealed class FlagExits
{
    // How many times one routine's answer may change before it is taken to know nothing. Answers
    // only lose what they keep, but a value can come and go while the routines it passes through
    // settle, so a cycle of routines is cut off rather than left to run on.
    private const int Changes = 8;

    private readonly Dictionary<RoutineKey, RoutineFlags> found;

    private FlagExits(Dictionary<RoutineKey, RoutineFlags> found) => this.found = found;

    /// <summary>
    /// Gets answers in which every routine returns with only what its signature says, for an
    /// analysis that runs before the program's answers are worked out.
    /// </summary>
    public static FlagExits None { get; } = new([]);

    /// <summary>
    /// Returns what a call to <paramref name="target"/> returns with. A routine worked out from
    /// its body returns with what was found. Any other routine, and a label inside one, returns
    /// with what the signature it is checked against says. An interrupt handler is never called,
    /// so a call to one is taken to know nothing.
    /// </summary>
    public RoutineFlags Of(Symbol target)
    {
        if (found.TryGetValue(RoutineKey.Of(target), out var flags))
            return flags;
        if (FlagAnalysis.SignatureOf(target) is not { IsInterrupt: false } signature)
            return RoutineFlags.Nothing;
        return new RoutineFlags(FlagsOf(signature.Keeps), signature.ExitFlags, StatusFlags.None, StatusFlags.None);
    }

    /// <summary>Returns the flags among C, Z, N and V that <paramref name="registers"/> names.</summary>
    internal static StatusFlags FlagsOf(Registers registers)
    {
        var flags = StatusFlags.None;
        foreach (var flag in new[] { StatusFlags.Carry, StatusFlags.Zero, StatusFlags.Negative, StatusFlags.Overflow })
        {
            if ((RegisterEffects.Of(flag) & registers) != Registers.None)
                flags |= flag;
        }
        return flags;
    }

    /// <summary>
    /// Works out the answer of every routine of <paramref name="files"/> that has a body, once
    /// what each keeps of the registers is settled. Each routine starts out keeping every flag,
    /// and is worked out again whenever a routine it calls changes.
    /// </summary>
    internal static FlagExits Solve(IReadOnlyList<FileAnalysis> files)
    {
        var regions = new Dictionary<RoutineKey, (FlowRegion Region, FlagAnalysis Flags)>();
        foreach (var file in files)
        {
            if (file.Flow.Flags is not { } analysis)
                continue;
            foreach (var region in file.Flow.Regions)
            {
                if (region.IsEntered && region.Routine.Signature is not { IsInterrupt: true })
                    regions.TryAdd(RoutineKey.Of(region.Routine), (region, analysis));
            }
        }

        // Which routines call each one, so that a change is passed on only to them.
        var callers = new Dictionary<RoutineKey, List<RoutineKey>>();
        foreach (var (key, (region, _)) in regions)
        {
            foreach (var block in region.Blocks)
            {
                foreach (var callee in block.RunsInto is { } runsInto ? block.Calls.Append(runsInto) : block.Calls)
                {
                    var owner = RegisterWalk.Owner(callee) ?? callee;
                    if (!callers.TryGetValue(RoutineKey.Of(owner), out var list))
                        callers[RoutineKey.Of(owner)] = list = [];
                    list.Add(key);
                }
            }
        }

        var exits = new FlagExits(regions.Keys.ToDictionary(key => key, _ => RoutineFlags.Everything));
        var changes = new Dictionary<RoutineKey, int>();
        var pending = new Queue<RoutineKey>(regions.Keys);
        var queued = new HashSet<RoutineKey>(regions.Keys);
        while (pending.TryDequeue(out var key))
        {
            queued.Remove(key);
            var (region, analysis) = regions[key];
            var answer = changes.GetValueOrDefault(key) > Changes
                ? RoutineFlags.Nothing
                : Answer(region, analysis.Summarize(region, exits.Of));
            if (answer.Equals(exits.found[key]))
                continue;
            exits.found[key] = answer;
            changes[key] = changes.GetValueOrDefault(key) + 1;
            foreach (var caller in callers.GetValueOrDefault(key) ?? [])
            {
                if (queued.Add(caller))
                    pending.Enqueue(caller);
            }
        }
        return exits;
    }

    /// <summary>
    /// Returns what a call to <paramref name="region"/>'s routine returns with, from the flags
    /// <paramref name="left"/> at its exits, which is null where no path leaves it. Which of C,
    /// Z, N and V it keeps comes from its registers, which follow saves on the stack the flags do
    /// not. A flag the routine itself declines to promise names no other routine as declining.
    /// </summary>
    private static RoutineFlags Answer(FlowRegion region, FlagState? left)
    {
        var routine = region.Routine;
        if (routine.Signature is { NeverReturns: true })
            return RoutineFlags.Nothing;
        if (left is null)
            return RoutineFlags.Everything;
        var nzcv = StatusFlags.Carry | StatusFlags.Zero | StatusFlags.Negative | StatusFlags.Overflow;
        var kept = (left.Kept & ~nzcv) | FlagsOf(region.Registers.Kept);
        var values = new FlagValues(left.Known, left.Set);
        var unbacked = (left.Unbacked & (values.Known | kept)) | (left.Quiet & values.Known);
        var decliners = ImmutableDictionary<StatusFlags, Symbol>.Empty;
        foreach (var flag in FlagValues.Named)
        {
            if ((unbacked & flag) != 0 && left.OriginOf(flag) is { } origin)
                decliners = decliners.SetItem(flag, origin);
        }

        // A routine that names a flag after `->` promises only the flags it names. It returns
        // what it declares even where its body fails to, which is reported where it does.
        if (routine.Signature is { DeclaresExitFlags: true } signature)
        {
            var promised = signature.ExitFlags;
            var others = (values.Known | (kept & ~nzcv)) & ~promised.Known;
            foreach (var flag in FlagValues.Named)
            {
                if ((others & flag) != 0)
                    decliners = decliners.Remove(flag);
            }
            unbacked = (unbacked | others) & ~promised.Known;
            values = new FlagValues(values.Known | promised.Known, (values.Set & ~promised.Known) | promised.Set);
        }
        var quiet = FlagsOf(region.Registers.Unbacked) & kept;
        return new RoutineFlags(kept, values, unbacked, quiet) { Decliners = decliners.IsEmpty ? null : decliners };
    }
}
