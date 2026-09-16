using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// One byte a routine has pushed, as far as the analysis can tell what it holds.
/// </summary>
/// <param name="IsStatus">Whether it is a status register a <c>php</c> saved, which a <c>plp</c> can restore.</param>
/// <param name="A">The accumulator's width saved in it, for a status byte.</param>
/// <param name="Index">The index registers' width saved in it, for a status byte.</param>
/// <param name="Frame">
/// The <c>.frame</c> whose lowest byte this is, which is where a slot's offset is counted
/// from; null for every other byte.
/// </param>
/// <param name="Held">
/// The value this byte is part of, for a push whose value the analysis knows: a D saved by
/// <c>phd</c>, a B saved by <c>phb</c>, the bank <c>phk</c> pushes, a <c>pea</c> of a constant, or a
/// constant loaded into A just before <c>pha</c>. A pull of the same size gets it back.
/// </param>
/// <param name="Size">How many bytes the push of <paramref name="Held"/> was; 0 for a byte that holds no known value.</param>
/// <param name="Byte">Which byte of that push this is, 0 for the low one, which is pushed last.</param>
public readonly record struct StackEntry(
    bool IsStatus, Width A, Width Index, Symbol? Frame = null, StateValue Held = default, int Size = 0, int Byte = 0)
{
    /// <summary>A byte the analysis knows nothing about.</summary>
    public static StackEntry Opaque => new(false, Width.Unknown, Width.Unknown, Held: StateValue.Unknown);
}
