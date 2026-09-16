using System.Collections.Immutable;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// What a routine has pushed since it was entered, one entry per byte, top last. A saved
/// status register is kept with the widths it saved, so a <c>php</c> … <c>plp</c> pair
/// restores them across calls and labels without either needing an annotation.
/// <para>
/// Two stacks are equal when they hold the same bytes, which is what a merge compares.
/// </para>
/// </summary>
public sealed class AnalysisStack : IEquatable<AnalysisStack>
{
    private readonly ImmutableArray<StackEntry> entries;

    private AnalysisStack(ImmutableArray<StackEntry> entries)
    {
        this.entries = entries;
    }

    /// <summary>What a routine has pushed when it is entered: nothing.</summary>
    public static AnalysisStack Empty { get; } = new([]);

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
        return new AnalysisStack(builder.ToImmutable());
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
        return new AnalysisStack(builder.ToImmutable());
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
        if (a is null || b is null || a.entries.Length != b.entries.Length)
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
        return new AnalysisStack(builder.ToImmutable());
    }

    /// <summary>
    /// The stack with its top <paramref name="size"/> bytes named as <paramref name="frame"/>,
    /// or null when fewer than that are on it. A frame named again moves to where it is named now.
    /// </summary>
    public AnalysisStack? Framed(Symbol frame, int size)
    {
        if (size > entries.Length)
            return null;
        var builder = entries.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            if (builder[i].Frame == frame)
                builder[i] = builder[i] with { Frame = null };
        }
        var bottom = entries.Length - size;
        builder[bottom] = builder[bottom] with { Frame = frame };
        return new AnalysisStack(builder.ToImmutable());
    }

    /// <summary>A stack of <paramref name="size"/> bytes named as <paramref name="frame"/>, with nothing known beneath them.</summary>
    public static AnalysisStack OnlyFrame(Symbol frame, int size) =>
        Empty.Push(StackEntry.Opaque, size).Framed(frame, size)!;

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
    /// The stack with <paramref name="count"/> bytes pulled, or null when the routine pulls
    /// more than it pushed: what is beneath is its caller's, and nothing is known of it.
    /// </summary>
    public AnalysisStack? Pull(int count) =>
        count > entries.Length ? null : new AnalysisStack(entries[..^count]);

    /// <inheritdoc/>
    public bool Equals(AnalysisStack? other) =>
        other is not null && entries.AsSpan().SequenceEqual(other.entries.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as AnalysisStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }
}
