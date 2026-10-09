using System.Collections.Immutable;
using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what a routine has pushed, as the <see cref="SourceWalk"/> follows it. It mirrors
/// <see cref="SavedStack"/>. A push saves where each value it moves was set, and the matching pull
/// gives that back, so a value passes through a <c>pha</c> … <c>pla</c> pair unchanged. A null
/// stack means nothing is known about it. Its <see cref="Height"/> is kept as
/// <see cref="SavedStack.Height"/> is.
/// </summary>
internal sealed class SourceStack : IEquatable<SourceStack>
{
    private readonly ImmutableArray<SourcePush> pushes;

    // How far the lowest push sits above the caller's stack as it was before the call, or null
    // where that is not known.
    private readonly int? offset;

    private SourceStack(ImmutableArray<SourcePush> pushes, int? offset)
    {
        this.pushes = pushes;
        this.offset = offset;
    }

    /// <summary>
    /// Gets the stack of a routine when it is entered, which holds no pushes, measured from its
    /// own entry rather than from a caller's stack.
    /// </summary>
    public static SourceStack Empty { get; } = new([], 0);

    /// <summary>Gets a value indicating whether nothing is on the stack.</summary>
    public bool IsEmpty => pushes.Length == 0;

    /// <summary>
    /// Gets how many bytes the stack holds above the caller's stack as it was before the call,
    /// which may be negative, or null where that is not known.
    /// </summary>
    public int? Height
    {
        get
        {
            var height = offset;
            foreach (var push in pushes)
                height += PushBytes.Of(push.Size, push.Width);
            return height;
        }
    }

    /// <summary>
    /// Returns the stack of a routine entered by a call, which holds no pushes and starts at the
    /// <paramref name="returnSize"/> bytes of the return address.
    /// </summary>
    public static SourceStack Entered(int returnSize) => new([], returnSize);

    /// <summary>
    /// Returns what two paths arriving at one place agree the stack holds, or null where they do not
    /// agree on what is on it. As with <see cref="SavedStack.Merge"/>, the two must have pushed the
    /// same things, and a push they disagree about holds what either saved. Two paths that agree on
    /// the pushes but not on the height leave the height unknown.
    /// </summary>
    public static SourceStack? Merge(SourceStack? a, SourceStack? b)
    {
        if (a is null || b is null || a.pushes.Length != b.pushes.Length)
            return null;
        if (a.Equals(b))
            return a;
        var offset = a.offset == b.offset ? a.offset : null;
        var builder = ImmutableArray.CreateBuilder<SourcePush>(a.pushes.Length);
        for (var i = 0; i < a.pushes.Length; i++)
        {
            if (SourcePush.Merge(a.pushes[i], b.pushes[i]) is not { } merged)
                return null;
            builder.Add(merged);
        }
        return new SourceStack(builder.MoveToImmutable(), offset);
    }

    /// <summary>Returns this stack with <paramref name="push"/> on top of it.</summary>
    public SourceStack Push(SourcePush push) => new(pushes.Add(push), offset);

    /// <summary>
    /// Returns the push a pull of this size and width takes back, or null where the pull does not
    /// match the push on top. The rule is <see cref="SavedStack.Pulled"/>'s, so two unknown widths
    /// never match.
    /// </summary>
    public SourcePush? Pulled(PushSize size, Semantics.Width width) =>
        pushes.Length > 0 && pushes[^1].Size == size && pushes[^1].Width == width && width != Semantics.Width.Unknown
            && !pushes[^1].IsLeft
            ? pushes[^1]
            : null;

    /// <summary>
    /// Returns this stack as a call to a routine with <paramref name="effect"/> leaves it once the
    /// routine returns, or null where what it leaves is not known. It follows
    /// <see cref="SavedStack.AfterCall"/>, and bytes the routine leaves saved nothing that can be
    /// followed. A pull of any size takes them.
    /// </summary>
    public SourceStack? AfterCall(StackEffect effect)
    {
        if (effect.KeepsTheStack)
            return this;
        if (effect.Kind == StackEffectKind.Unknown || (effect.Bytes < 0 && pushes.Length > 0))
            return null;
        if (effect.Bytes < 0)
            return new SourceStack(pushes, offset + effect.Bytes);
        var left = pushes.ToBuilder();
        for (var i = 0; i < effect.Bytes; i++)
            left.Add(new SourcePush([], PushSize.OneByte, Semantics.Width.Eight) { IsLeft = true });
        return new SourceStack(left.ToImmutable(), offset);
    }

    /// <summary>
    /// Returns this stack with its top push taken off, or null where the pull does not match that
    /// push. As with <see cref="SavedStack.Pull"/>, a pull from an empty stack leaves it empty and
    /// lowers its height. A pull takes as many of the bytes a called routine left as it is wide.
    /// </summary>
    public SourceStack? Pull(PushSize size, Semantics.Width width)
    {
        if (pushes.Length > 0 && pushes[^1].IsLeft)
        {
            if (PushBytes.Of(size, width) is not { } bytes)
                return null;
            var below = pushes.Length;
            while (bytes > 0 && below > 0 && pushes[below - 1].IsLeft)
                (below, bytes) = (below - 1, bytes - 1);
            return bytes == 0 || below == 0 ? new SourceStack(pushes[..below], offset - bytes) : null;
        }
        return pushes.Length == 0 ? new SourceStack(pushes, offset - PushBytes.Of(size, width))
            : pushes[^1].Size == size && pushes[^1].Width == width ? new SourceStack(pushes[..^1], offset)
            : null;
    }

    /// <inheritdoc/>
    public bool Equals(SourceStack? other) =>
        other is not null && offset == other.offset && pushes.AsSpan().SequenceEqual(other.pushes.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SourceStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(offset);
        foreach (var push in pushes)
            hash.Add(push);
        return hash.ToHashCode();
    }
}
