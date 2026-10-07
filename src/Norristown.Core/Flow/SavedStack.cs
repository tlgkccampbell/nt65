using System.Collections.Immutable;
using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents what a routine has pushed, as it bears on saving and restoring a register. It
/// holds one entry per push, with the top last. A <c>pha</c> records what the accumulator holds,
/// and the matching <c>pla</c> takes it back, so a save and a restore cancel out even with a
/// call or a label between them.
/// <para>
/// This is not the 65816's <see cref="AnalysisStack"/>. That one tracks a saved status
/// register, direct page and data bank by value, to work out the widths and the banks. This one
/// tracks which register's entry value each push holds, to work out which registers a routine
/// returns unchanged. A null <see cref="SavedStack"/> means nothing is known about the stack,
/// which is the case after <c>txs</c> and where two paths that pushed different amounts meet.
/// </para>
/// <para>
/// Apart from its pushes, the stack keeps its <see cref="Height"/> in bytes above the caller's
/// stack as it was before the call, as <see cref="AnalysisStack.Height"/> does.
/// </para>
/// </summary>
public sealed class SavedStack : IEquatable<SavedStack>
{
    private readonly ImmutableArray<SavedPush> pushes;

    // How far the lowest push sits above the caller's stack as it was before the call, or null
    // where that is not known.
    private readonly int? offset;

    private SavedStack(ImmutableArray<SavedPush> pushes, int? offset)
    {
        this.pushes = pushes;
        this.offset = offset;
    }

    /// <summary>
    /// Gets the stack of a routine when it is entered, which holds no pushes, measured from its
    /// own entry rather than from a caller's stack.
    /// </summary>
    public static SavedStack Empty { get; } = new([], 0);

    /// <summary>Gets how many pushes are on the stack.</summary>
    public int Depth => pushes.Length;

    /// <summary>
    /// Gets the pushes on the stack, deepest first, for an editor that lists what a routine is
    /// holding.
    /// </summary>
    public IReadOnlyList<SavedPush> Pushes => pushes;

    /// <summary>
    /// Gets how many bytes the stack holds above the caller's stack as it was before the call,
    /// which may be negative, or null where that is not known. A push of a register whose width is
    /// not known leaves it unknown.
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

    /// <summary>Gets the registers whose entry values any push on the stack may hold.</summary>
    public Registers Entries
    {
        get
        {
            var entries = Registers.None;
            foreach (var push in pushes)
                entries |= push.Value.Entry;
            return entries;
        }
    }

    /// <summary>
    /// Returns the stack of a routine entered by a call, which holds no pushes and starts at the
    /// <paramref name="returnSize"/> bytes of the return address.
    /// </summary>
    public static SavedStack Entered(int returnSize) => new([], returnSize);

    /// <summary>
    /// Returns what two paths arriving at one place agree the stack holds, or null when they do
    /// not agree on what is on it. A push they disagree about holds what either of them left. Two
    /// paths that agree on the pushes but not on the height leave the height unknown.
    /// </summary>
    public static SavedStack? Merge(SavedStack? a, SavedStack? b)
    {
        if (a is null || b is null || a.pushes.Length != b.pushes.Length)
            return null;
        if (a.Equals(b))
            return a;
        var offset = a.offset == b.offset ? a.offset : null;
        var builder = a.pushes.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            var (x, y) = (a.pushes[i], b.pushes[i]);
            if (x.Size != y.Size || x.Width != y.Width)
                return null;
            builder[i] = x with { Value = RegisterValue.Merge(x.Value, y.Value) };
        }
        return new SavedStack(builder.ToImmutable(), offset);
    }

    /// <summary>Returns this stack with <paramref name="push"/> on top of it.</summary>
    public SavedStack Push(SavedPush push) => new(pushes.Add(push), offset);

    /// <summary>
    /// Returns what a pull of this size and width gets back. That is what the push on top holds
    /// when it was the same size and width, and otherwise a value nothing is known about. A pull
    /// of something else, or of more than the routine pushed, reaches bytes that are not its own.
    /// <para>
    /// Two unknown widths are not taken to be the same width. A call between the push and the
    /// pull may have widened the register, and then the pull takes back bytes the push never
    /// put there. <see cref="Semantics.Width.Unchanged"/> does not have this problem, because
    /// a routine that leaves a register's width as it found it leaves the same width at both.
    /// </para>
    /// </summary>
    public RegisterValue Pulled(PushSize size, Semantics.Width width) =>
        pushes.Length > 0 && pushes[^1].Size == size && pushes[^1].Width == width
            && width != Semantics.Width.Unknown
            ? pushes[^1].Value
            : RegisterValue.Unknown;

    /// <summary>
    /// Returns this stack with its top push taken off, or null when the pull does not match that
    /// push, which leaves nothing known about the stack.
    /// <para>
    /// A pull from an empty stack takes bytes the caller put there, such as the return address
    /// a routine pulls to read what follows its call. What it gets is unknown, but the stack
    /// stays empty rather than unknown, so what the routine pushes and pulls after it is still
    /// followed. Such a pull lowers the <see cref="Height"/> below the return address. Two paths
    /// that meet having pulled different amounts both hold nothing known, and every pull past
    /// what they push again gets back nothing known either.
    /// </para>
    /// </summary>
    public SavedStack? Pull(PushSize size, Semantics.Width width) =>
        pushes.Length == 0 ? new SavedStack(pushes, offset - PushBytes.Of(size, width))
            : pushes[^1].Size == size && pushes[^1].Width == width ? new SavedStack(pushes[..^1], offset)
            : null;

    /// <inheritdoc/>
    public bool Equals(SavedStack? other) =>
        other is not null && offset == other.offset && pushes.AsSpan().SequenceEqual(other.pushes.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SavedStack);

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
