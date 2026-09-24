using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents one byte a routine has pushed, with what the analysis can tell about what it holds.
/// </summary>
/// <param name="IsStatus">Whether it is a status register a <c>php</c> saved, which a <c>plp</c> can restore.</param>
/// <param name="A">The accumulator's width saved in it, for a status byte.</param>
/// <param name="Index">The index registers' width saved in it, for a status byte.</param>
/// <param name="Frame">
/// The <c>.frame</c> whose lowest byte this is, from which a slot's offset is counted; null for
/// every other byte.
/// </param>
/// <param name="Held">
/// The value this byte is part of, for a push whose value the analysis knows. Such a value is a
/// D saved by <c>phd</c>, a B saved by <c>phb</c>, the bank <c>phk</c> pushes, a <c>pea</c> of a
/// constant, or a constant loaded into A just before <c>pha</c>. A pull of the same size gets it
/// back.
/// </param>
/// <param name="Size">
/// How many bytes the push of <paramref name="Held"/> was, or 0 for a byte that holds no known
/// value.
/// </param>
/// <param name="Byte">Which byte of that push this is, where 0 is the low byte, which is pushed last.</param>
public readonly record struct StackEntry(
    bool IsStatus, Width A, Width Index, Symbol? Frame = null, StateValue Held = default, int Size = 0, int Byte = 0)
{
    /// <summary>Gets a byte the analysis knows nothing about.</summary>
    public static StackEntry Opaque => new(false, Width.Unknown, Width.Unknown, Held: StateValue.Unknown);
}
