namespace Norristown.Flow;

/// <summary>
/// Represents a store through an indexed operand, such as <c>sta buf-1,x</c>, as the memory part
/// of <see cref="InputSources"/> follows it. The operand names only where the store starts, so the
/// store may land on any byte the index register can reach from there.
/// </summary>
/// <param name="Start">The location the operand names.</param>
/// <param name="Index">
/// The constant the index register holds, where the instructions on every path give it one, or
/// null. A known constant narrows the store to one byte.
/// </param>
/// <param name="Reach">The largest value the index register can hold.</param>
internal readonly record struct IndexedStore(Location Start, long? Index, long Reach)
{
    /// <summary>
    /// Returns whether the store may land on <paramref name="location"/>.
    /// </summary>
    /// <remarks>
    /// With a known index, the store lands on one byte, which may be spelled another way. Without
    /// one, it may land on any byte of the symbol it starts from, and on any location declared in
    /// the same segment, since the linker puts that segment's bytes next to one another. Where the
    /// source fixes both addresses, it may land on a location within reach of the index. Where
    /// only one address is fixed, nothing says how far apart the two are, so the store is not
    /// taken to land there.
    /// </remarks>
    /// <param name="location">The location that may be changed.</param>
    /// <param name="anchor">
    /// Returns the location a spelling stands for once each address alias is followed to the
    /// location it names, as <see cref="MemoryInference.Anchor"/> does.
    /// </param>
    public bool Reaches(Location location, Func<Location, Location> anchor)
    {
        var start = anchor(Start);
        var other = anchor(location);
        if (Index is { } index)
        {
            var landed = start with { Offset = start.Offset + index };
            return landed == other || (landed.Address is { } at && at == other.Address);
        }
        if (start.Group == other.Group || Start.Group == location.Group)
            return true;
        if (start.Address is { } first && other.Address is { } address)
            return address >= first && address <= first + Reach;
        return start.Root?.Segment is { } segment && other.Root?.Segment == segment;
    }
}
