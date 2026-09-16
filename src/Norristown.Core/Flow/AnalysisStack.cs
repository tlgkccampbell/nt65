using System.Collections.Immutable;

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
