using System.Collections.Immutable;
using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what a routine has pushed, as the <see cref="SourceWalk"/> follows it. It mirrors
/// <see cref="SavedStack"/>. A push saves where each value it moves was set, and the matching pull
/// gives that back, so a value passes through a <c>pha</c> … <c>pla</c> pair unchanged. A null
/// stack means nothing is known about it.
/// </summary>
internal sealed class SourceStack : IEquatable<SourceStack>
{
    private readonly ImmutableArray<SourcePush> pushes;

    private SourceStack(ImmutableArray<SourcePush> pushes) => this.pushes = pushes;

    /// <summary>Gets the stack of a routine when it is entered, which holds no pushes.</summary>
    public static SourceStack Empty { get; } = new([]);

    /// <summary>Gets a value indicating whether nothing is on the stack.</summary>
    public bool IsEmpty => pushes.Length == 0;

    /// <summary>
    /// Returns what two paths arriving at one place agree the stack holds, or null where they do not
    /// agree on what is on it. As with <see cref="SavedStack.Merge"/>, the two must have pushed the
    /// same things, and a push they disagree about holds what either saved.
    /// </summary>
    public static SourceStack? Merge(SourceStack? a, SourceStack? b)
    {
        if (a is null || b is null || a.pushes.Length != b.pushes.Length)
            return null;
        if (a.Equals(b))
            return a;
        var builder = ImmutableArray.CreateBuilder<SourcePush>(a.pushes.Length);
        for (var i = 0; i < a.pushes.Length; i++)
        {
            if (SourcePush.Merge(a.pushes[i], b.pushes[i]) is not { } merged)
                return null;
            builder.Add(merged);
        }
        return new SourceStack(builder.MoveToImmutable());
    }

    /// <summary>Returns this stack with <paramref name="push"/> on top of it.</summary>
    public SourceStack Push(SourcePush push) => new(pushes.Add(push));

    /// <summary>
    /// Returns the push a pull of this size and width takes back, or null where the pull does not
    /// match the push on top. The rule is <see cref="SavedStack.Pulled"/>'s, so two unknown widths
    /// never match.
    /// </summary>
    public SourcePush? Pulled(PushSize size, Semantics.Width width) =>
        pushes.Length > 0 && pushes[^1].Size == size && pushes[^1].Width == width && width != Semantics.Width.Unknown
            ? pushes[^1]
            : null;

    /// <summary>
    /// Returns this stack with its top push taken off, or null where the pull does not match that
    /// push. As with <see cref="SavedStack.Pull"/>, a pull from an empty stack leaves it empty.
    /// </summary>
    public SourceStack? Pull(PushSize size, Semantics.Width width) =>
        pushes.Length == 0 ? this
            : pushes[^1].Size == size && pushes[^1].Width == width ? new SourceStack(pushes[..^1])
            : null;

    /// <inheritdoc/>
    public bool Equals(SourceStack? other) => other is not null && pushes.AsSpan().SequenceEqual(other.pushes.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SourceStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var push in pushes)
            hash.Add(push);
        return hash.ToHashCode();
    }
}
