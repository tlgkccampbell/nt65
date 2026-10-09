using System.Collections.Immutable;
using Norristown.Processor;

namespace Norristown.Flow;

/// <summary>
/// Represents one push on a <see cref="SourceStack"/>, with where each value it saved was set.
/// </summary>
/// <param name="Values">
/// The values the push saved. A push of a register saves one. A <c>php</c> saves one per flag, in
/// the order <see cref="SourceState.Flags"/> lists them. A push of anything else saves none.
/// </param>
/// <param name="Size">How much of the stack the push takes.</param>
/// <param name="Width">How wide the register it moved was, which is 8 bits wherever only one width is possible.</param>
internal sealed record SourcePush(ImmutableArray<SourceValue> Values, PushSize Size, Semantics.Width Width)
{
    /// <summary>
    /// Gets a value indicating whether this is one byte a called routine left on the stack, as
    /// <see cref="SavedPush.IsLeft"/> is.
    /// </summary>
    public bool IsLeft { get; init; }

    /// <summary>
    /// Returns what two paths agree the push holds, or null where they disagree about its size or
    /// its width. Where they pushed different things of the same size, as <c>php</c> on one path and
    /// <c>phb</c> on the other, the push saved nothing that can be followed.
    /// </summary>
    public static SourcePush? Merge(SourcePush a, SourcePush b)
    {
        if (a.Size != b.Size || a.Width != b.Width || a.IsLeft != b.IsLeft)
            return null;
        if (a.Equals(b))
            return a;
        if (a.Values.Length != b.Values.Length)
            return a with { Values = [] };
        var values = ImmutableArray.CreateBuilder<SourceValue>(a.Values.Length);
        for (var i = 0; i < a.Values.Length; i++)
            values.Add(SourceValue.Merge(a.Values[i], b.Values[i]));
        return a with { Values = values.MoveToImmutable() };
    }

    /// <inheritdoc/>
    public bool Equals(SourcePush? other) =>
        other is not null && Size == other.Size && Width == other.Width && IsLeft == other.IsLeft && Values.AsSpan().SequenceEqual(other.Values.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = HashCode.Combine(Size, Width, IsLeft);
        foreach (var value in Values)
            hash = HashCode.Combine(hash, value);
        return hash;
    }
}
