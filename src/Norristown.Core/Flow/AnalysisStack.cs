using System.Collections.Immutable;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents the bytes a routine has pushed, one entry per byte, with the top of the stack
/// last. The entries sit on a base, which starts as the stack pointer at routine entry. The base
/// stays there until a <c>txs</c> or <c>tcs</c> moves the stack to an unknown place, or a pull
/// takes more than is known. After that, pushes are still tracked on top of an unknown base, and
/// a pull still finds what they pushed. A saved status register is kept with the widths it
/// saved, so a <c>php</c> … <c>plp</c> pair restores them across calls and labels without
/// either needing an annotation.
/// <para>
/// Apart from what is on it, the stack keeps its <see cref="Height"/> above the caller's stack
/// as it was before the call, which is how much a return there leaves the caller. A pull of more
/// than is known still lowers the height, so a routine that pulls its own return address is
/// still measured.
/// </para>
/// <para>
/// Two stacks are equal when they hold the same bytes over the same kind of base. A merge
/// compares stacks this way.
/// </para>
/// </summary>
public sealed class AnalysisStack : IEquatable<AnalysisStack>
{
    private readonly ImmutableArray<StackEntry> entries;

    // How far the lowest entry sits above the caller's stack as it was before the call, or null
    // where that is not known.
    private readonly int? offset;

    private AnalysisStack(ImmutableArray<StackEntry> entries, bool isAnchored, int? offset)
    {
        this.entries = entries;
        IsAnchored = isAnchored;
        this.offset = offset;
    }

    /// <summary>
    /// Gets the stack of a routine when it is entered, which holds nothing, measured from its own
    /// entry rather than from a caller's stack.
    /// </summary>
    public static AnalysisStack Empty { get; } = new([], true, 0);

    /// <summary>
    /// Gets a stack about which nothing is known, but on which pushes can be tracked. A
    /// <c>txs</c> leaves the stack in this state.
    /// </summary>
    public static AnalysisStack Unanchored { get; } = new([], false, null);

    /// <summary>
    /// Gets a value indicating whether the base is the stack pointer at routine entry. The stack
    /// then holds everything pushed since entry, and nothing beneath it is the routine's.
    /// </summary>
    public bool IsAnchored { get; }

    /// <summary>Gets how many bytes are on the stack.</summary>
    public int Depth => entries.Length;

    /// <summary>
    /// Gets how many bytes the stack holds above the caller's stack as it was before the call,
    /// which may be negative, or null where that is not known. A routine entered by a call starts
    /// at the size of its return address, and a return that leaves nothing behind ends at it.
    /// </summary>
    public int? Height => offset + entries.Length;

    /// <summary>Gets the byte on top, or null when there is none.</summary>
    public StackEntry? Top => entries.IsEmpty ? null : entries[^1];

    /// <summary>
    /// Gets the bytes on the stack, deepest first, for an editor that lists what a routine is
    /// holding. The <see cref="SavedStack"/> has one entry per push and this one has one per
    /// byte, so a reader that wants pushes groups these by each entry's size.
    /// </summary>
    public IReadOnlyList<StackEntry> Entries => entries;

    /// <summary>
    /// Returns the stack of a routine entered by a call, which holds the caller's
    /// <paramref name="arguments"/> bytes and the <paramref name="returnSize"/> bytes of the return
    /// address. The arguments are the caller's, so they sit below the caller's stack as the
    /// <see cref="Height"/> counts it.
    /// </summary>
    public static AnalysisStack Entered(int returnSize, int arguments) => arguments > 0
        ? new AnalysisStack([.. Enumerable.Repeat(StackEntry.Opaque, arguments + returnSize)], true, -arguments)
        : new AnalysisStack([], true, returnSize);

    /// <summary>
    /// Returns what two paths arriving at one place agree the stack holds, or null when they do
    /// not agree on its shape. A byte whose value differs between them becomes a byte nothing is
    /// known about, which leaves the depth, the saved status registers and the frames known. Two
    /// paths that agree on the shape but not on the height leave the height unknown.
    /// </summary>
    public static AnalysisStack? Merge(AnalysisStack? a, AnalysisStack? b)
    {
        if (a is null || b is null || a.entries.Length != b.entries.Length || a.IsAnchored != b.IsAnchored)
            return null;
        if (a.Equals(b))
            return a;
        var offset = a.offset == b.offset ? a.offset : null;
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
        return new AnalysisStack(builder.ToImmutable(), a.IsAnchored, offset);
    }

    /// <summary>
    /// Returns a stack of <paramref name="size"/> bytes named as <paramref name="frame"/>, with
    /// nothing known beneath them.
    /// </summary>
    public static AnalysisStack OnlyFrame(Symbol frame, int size) =>
        Unanchored.Framed(frame, size)!;

    /// <summary>Returns this stack with <paramref name="entry"/> pushed <paramref name="count"/> times.</summary>
    public AnalysisStack Push(StackEntry entry, int count = 1)
    {
        var builder = entries.ToBuilder();
        for (var i = 0; i < count; i++)
            builder.Add(entry);
        return new AnalysisStack(builder.ToImmutable(), IsAnchored, offset);
    }

    /// <summary>
    /// Returns this stack with a value <paramref name="size"/> bytes wide pushed, high byte first
    /// as the processor pushes it. A value the analysis does not know is pushed as bytes it knows
    /// nothing about.
    /// </summary>
    public AnalysisStack PushValue(StateValue held, int size)
    {
        if (held.Kind == StateValueKind.Unknown)
            return Push(StackEntry.Opaque, size);
        var builder = entries.ToBuilder();
        for (var i = size - 1; i >= 0; i--)
            builder.Add(new StackEntry(false, Width.Unknown, Width.Unknown, Held: held, Size: size, Byte: i));
        return new AnalysisStack(builder.ToImmutable(), IsAnchored, offset);
    }

    /// <summary>
    /// Returns what a pull of <paramref name="size"/> bytes gets back. That is the value a push of
    /// the same size left on top, or unknown when the top bytes are anything else.
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
    /// Returns this stack with its top <paramref name="size"/> bytes named as
    /// <paramref name="frame"/>, or null when fewer than that have been pushed since the routine
    /// was entered. Over an unknown base the frame reaches into bytes nothing is known about. A
    /// frame named again moves to where it is named now. Naming bytes moves nothing, so the
    /// <see cref="Height"/> stays as it was.
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
        return new AnalysisStack(builder.ToImmutable(), IsAnchored, offset - Math.Max(0, size - entries.Length));
    }

    /// <summary>
    /// Returns how many bytes are above the lowest byte of <paramref name="frame"/>, or null when
    /// the frame is not on the stack.
    /// </summary>
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
    /// Returns this stack as a call to a routine with <paramref name="effect"/> leaves it once the
    /// routine returns, or null where what it leaves is not known. Bytes the routine leaves are
    /// bytes nothing is known about, and bytes it takes come off the top.
    /// </summary>
    public AnalysisStack? AfterCall(StackEffect effect) =>
        effect.KeepsTheStack ? this
            : effect.Kind == StackEffectKind.Unknown ? null
            : effect.Bytes > 0 ? Push(StackEntry.Opaque, effect.Bytes)
            : Pull(-effect.Bytes);

    /// <summary>
    /// Returns this stack with <paramref name="count"/> bytes pulled. A pull of more than is known
    /// takes what is beneath, which belongs to the caller or is unknown, and leaves a stack about
    /// which nothing is known but its <see cref="Height"/>.
    /// </summary>
    public AnalysisStack Pull(int count) => count > entries.Length
        ? new AnalysisStack([], false, Height - count)
        : new AnalysisStack(entries[..^count], IsAnchored, offset);

    /// <inheritdoc/>
    public bool Equals(AnalysisStack? other) =>
        other is not null && IsAnchored == other.IsAnchored && offset == other.offset
        && entries.AsSpan().SequenceEqual(other.entries.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as AnalysisStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsAnchored);
        hash.Add(offset);
        foreach (var entry in entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }
}
