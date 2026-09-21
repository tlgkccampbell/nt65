using System.Collections.Immutable;

namespace Norristown.Flow;

/// <summary>
/// What a routine has pushed, as far as saving and restoring a register goes: one entry per
/// push, top last. A <c>pha</c> puts what the accumulator holds on it and the <c>pla</c> that
/// finds it again takes it back, which is how a save and a restore cancel with a call or a
/// label between them.
/// <para>
/// This is not the 65816's analysis stack. That one follows a saved status register, a direct
/// page and a data bank by value, to say what the widths and the banks are; this one follows
/// whose entry value a push holds, to say what a routine hands back. A null stack stands for
/// one nothing is known of, which is where <c>txs</c> leaves it and where two paths that
/// pushed different amounts meet.
/// </para>
/// </summary>
public sealed class SavedStack : IEquatable<SavedStack>
{
    private readonly ImmutableArray<SavedPush> pushes;

    private SavedStack(ImmutableArray<SavedPush> pushes) => this.pushes = pushes;

    /// <summary>What a routine has pushed when it is entered: nothing.</summary>
    public static SavedStack Empty { get; } = new([]);

    /// <summary>How many pushes are on it.</summary>
    public int Depth => pushes.Length;

    /// <summary>
    /// The pushes on it, deepest first, for an editor that lists what a routine is holding.
    /// </summary>
    public IReadOnlyList<SavedPush> Pushes => pushes;

    /// <summary>The stack with <paramref name="push"/> on top of it.</summary>
    public SavedStack Push(SavedPush push) => new(pushes.Add(push));

    /// <summary>
    /// What a pull of this size and width gets back: what the push on top holds when it was
    /// the same size and width, and otherwise nothing known. A pull of something else, or of
    /// more than the routine pushed, reaches bytes that are not its own.
    /// <para>
    /// A width nobody knows is not the same width twice: a call between the push and the pull
    /// may have widened the register, and then the pull takes back bytes the push never put
    /// there. Unchanged is not that case, because a routine that hands a register back as it
    /// found it hands its width back too.
    /// </para>
    /// </summary>
    public RegisterValue Pulled(PushSize size, Semantics.Width width) =>
        pushes.Length > 0 && pushes[^1].Size == size && pushes[^1].Width == width
            && width != Semantics.Width.Unknown
            ? pushes[^1].Value
            : RegisterValue.Unknown;

    /// <summary>
    /// The stack with its top push taken off, or null when it holds none or the pull does not
    /// match it, which leaves the stack somewhere nothing is known of.
    /// </summary>
    public SavedStack? Pull(PushSize size, Semantics.Width width) =>
        pushes.Length > 0 && pushes[^1].Size == size && pushes[^1].Width == width
            ? new SavedStack(pushes[..^1])
            : null;

    /// <summary>
    /// What two paths arriving at one place agree the stack holds, or null when they do not
    /// agree on what is on it. A push they disagree about holds what either of them left.
    /// </summary>
    public static SavedStack? Merge(SavedStack? a, SavedStack? b)
    {
        if (a is null || b is null || a.pushes.Length != b.pushes.Length)
            return null;
        if (a.Equals(b))
            return a;
        var builder = a.pushes.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            var (x, y) = (a.pushes[i], b.pushes[i]);
            if (x.Size != y.Size || x.Width != y.Width)
                return null;
            builder[i] = x with { Value = RegisterValue.Merge(x.Value, y.Value) };
        }
        return new SavedStack(builder.ToImmutable());
    }

    /// <inheritdoc/>
    public bool Equals(SavedStack? other) => other is not null && pushes.AsSpan().SequenceEqual(other.pushes.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SavedStack);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var push in pushes)
            hash.Add(push);
        return hash.ToHashCode();
    }
}
