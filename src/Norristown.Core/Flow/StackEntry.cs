using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// One byte a routine has pushed, as far as the analysis can tell what it holds.
/// </summary>
/// <param name="IsStatus">Whether it is a status register a <c>php</c> saved, which a <c>plp</c> can restore.</param>
/// <param name="A">The accumulator's width saved in it, for a status byte.</param>
/// <param name="Index">The index registers' width saved in it, for a status byte.</param>
public readonly record struct StackEntry(bool IsStatus, Width A, Width Index)
{
    /// <summary>A byte the analysis knows nothing about.</summary>
    public static StackEntry Opaque => new(false, Width.Unknown, Width.Unknown);
}
