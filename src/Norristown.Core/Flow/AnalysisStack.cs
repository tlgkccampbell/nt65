using System.Collections.Immutable;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// What a routine has pushed, one entry per byte, top last: a known top over a base. The base
/// is where the routine was entered, until a <c>txs</c> or <c>tcs</c> moves the stack somewhere
/// nothing is known of, or a pull takes more than is known; after that pushes are still
/// tracked on top of an unknown base, and a pull still finds what they pushed. A saved status
/// register is kept with the widths it saved, so a <c>php</c> … <c>plp</c> pair restores them
/// across calls and labels without either needing an annotation.
/// <para>
/// Two stacks are equal when they hold the same bytes over the same kind of base, which is what
/// a merge compares.
/// </para>
/// </summary>
public sealed class AnalysisStack : IEquatable<AnalysisStack>
{
    private readonly ImmutableArray<StackEntry> entries;

    private AnalysisStack(ImmutableArray<StackEntry> entries, bool isAnchored)
    {
        this.entries = entries;
        IsAnchored = isAnchored;
    }

    /// <summary>What a routine has pushed when it is entered: nothing.</summary>
    public static AnalysisStack Empty { get; } = new([], true);

    /// <summary>A stack of which nothing is known, but on which pushes can be tracked: where <c>txs</c> leaves it.</summary>
    public static AnalysisStack Unanchored { get; } = new([], false);

    /// <summary>
    /// Whether the base is where the routine was entered, so the stack holds everything pushed
    /// since then and nothing beneath it is the routine's.
    /// </summary>
    public bool IsAnchored { get; }

    /// <summary>How many bytes are on it.</summary>
    public int Depth => entries.Length;

    /// <summary>The byte on top, or null when there is none.</summary>
    public StackEntry? Top => entries.IsEmpty ? null : entries[^1];

    /// <summary>The stack with <paramref name="entry"/> pushed <paramref name="count"/> times.</summary>
    public AnalysisStack Push(StackEntry entry, int count = 1)
    {
        var builder = entries.ToBuilder();
        for (var i = 0; i < count; i++)
            builder.Add(entry);
        return new AnalysisStack(builder.ToImmutable(), IsAnchored);
    }

    /// <summary>
    /// The stack with a value <paramref name="size"/> bytes wide pushed, high byte first as the
    /// processor pushes it. A value the analysis does not know is pushed as bytes it knows nothing about.
    /// </summary>
    public AnalysisStack PushValue(StateValue held, int size)
    {
        if (held.Kind == StateValueKind.Unknown)
            return Push(StackEntry.Opaque, size);
        var builder = entries.ToBuilder();
        for (var i = size - 1; i >= 0; i--)
            builder.Add(new StackEntry(false, Width.Unknown, Width.Unknown, Held: held, Size: size, Byte: i));
        return new AnalysisStack(builder.ToImmutable(), IsAnchored);
    }

    /// <summary>
    /// What a pull of <paramref name="size"/> bytes gets back: the value a push of the same size
    /// left on top, or unknown when the top bytes are anything else.
    /// </summary>
    public StateValue PulledValue(int size)
    {
        if (size > entries.Length)
            return StateValue.Unknown;
        var top = entries[^1];
        if (top.Size != size)
            return StateValue.Unknown;
        for (var i = 0; i < size; i++)
        {
            var entry = entries[entries.Length - 1 - i];
            if (entry.Size != size || entry.Byte != i || entry.Held != top.Held)
                return StateValue.Unknown;
        }
        return top.Held;
    }

    /// <summary>
    /// What two paths arriving at one place agree the stack holds, or null when they do not
    /// agree on its shape. A byte whose value differs between them is a byte nothing is known
    /// about, which leaves the depth, the saved status registers and the frames known.
    /// </summary>
    public static AnalysisStack? Merge(AnalysisStack? a, AnalysisStack? b)
    {
        if (a is null || b is null || a.entries.Length != b.entries.Length || a.IsAnchored != b.IsAnchored)
            return null;
        if (a.Equals(b))
            return a;
        var builder = a.entries.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            var (x, y) = (a.entries[i], b.entries[i]);
            if (x == y)
                continue;
            if (x.IsStatus || y.IsStatus || x.Frame != y.Frame)
                return null;
            builder[i] = StackEntry.Opaque with { Frame = x.Frame };
        }
        return new AnalysisStack(builder.ToImmutable(), a.IsAnchored);
    }

    /// <summary>
    /// The stack with its top <paramref name="size"/> bytes named as <paramref name="frame"/>,
    /// or null when fewer than that are pushed since the routine was entered. Over an unknown
    /// base the frame reaches into bytes nothing is known of. A frame named again moves to where
    /// it is named now.
    /// </summary>
    public AnalysisStack? Framed(Symbol frame, int size)
    {
        if (size > entries.Length && IsAnchored)
            return null;
        var builder = entries.ToBuilder();
        builder.InsertRange(0, Enumerable.Repeat(StackEntry.Opaque, Math.Max(0, size - entries.Length)));
        for (var i = 0; i < builder.Count; i++)
        {
            if (builder[i].Frame == frame)
                builder[i] = builder[i] with { Frame = null };
        }
        var bottom = builder.Count - size;
        builder[bottom] = builder[bottom] with { Frame = frame };
        return new AnalysisStack(builder.ToImmutable(), IsAnchored);
    }

    /// <summary>A stack of <paramref name="size"/> bytes named as <paramref name="frame"/>, with nothing known beneath them.</summary>
    public static AnalysisStack OnlyFrame(Symbol frame, int size) =>
        Unanchored.Framed(frame, size)!;

    /// <summary>How many bytes are above the lowest byte of <paramref name="frame"/>, or null when it is not on the stack.</summary>
    public int? Above(Symbol frame)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Frame == frame)
                return entries.Length - i - 1;
        }
        return null;
    }

    /// <summary>
    /// The stack with <paramref name="count"/> bytes pulled. A pull of more than is known takes
    /// what is beneath, which is its caller's or unknown, and leaves a stack of which nothing is known.
    /// </summary>
    public AnalysisStack Pull(int count) =>
        count > entries.Length ? Unanchored : new AnalysisStack(entries[..^count], IsAnchored);

    /// <inheritdoc/>
    public bool Equals(AnalysisStack? other) =>
        other is not null && IsAnchored == other.IsAnchored && entries.AsSpan().SequenceEqual(other.entries.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as AnalysisStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsAnchored);
        foreach (var entry in entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }
}
