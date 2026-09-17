namespace Norristown.Semantics;

/// <summary>
/// One segment of the program: its name and the address size every symbol in it
/// gets. A segment is declared exactly once, so this is what references to it are sized from.
/// </summary>
/// <param name="Name">The name as it is written in quotes.</param>
/// <param name="Size">The address size of symbols in the segment.</param>
/// <param name="Declaration">Where it was declared, or null for one of the standard names.</param>
/// <param name="DirectPage">
/// The <c>dp = e</c> it declares: the direct page its symbols are meant to be reached through,
/// on the 65816. Null where it declares none, and nothing about D is then checked against it.
/// </param>
/// <param name="Bank">
/// The <c>bank = e</c> it declares: its home bank, where it lives and where code in it is taken to
/// run, on the 65816. Null where it declares none.
/// </param>
public sealed record Segment(string Name, AddressSize Size, Span? Declaration, long? DirectPage = null, long? Bank = null)
{
    /// <summary>
    /// The banks its <c>mirrors</c> declares, each a range: other banks the same memory is seen
    /// in, such as low WRAM in banks <c>$00-$3f</c>. An absolute operand reaches its symbols
    /// with the data bank at the home bank or any of these.
    /// </summary>
    public IReadOnlyList<(long First, long Last)> Mirrors { get; init; } = [];

    /// <summary>Whether its symbols are reached with the data bank at <paramref name="bank"/>: its home bank or a mirror.</summary>
    public bool IsSeenFrom(long bank) =>
        Bank == bank || Mirrors.Any(mirror => bank >= mirror.First && bank <= mirror.Last);

    /// <summary>
    /// Where it is, as a message says it: <c>in bank $7e</c>, or <c>in bank $7e and mirrored in
    /// banks $00-$3f, $80-$bf</c>.
    /// </summary>
    public string SpellBanks() =>
        $"in bank {StateValue.Hex(Bank ?? 0, 2)}"
        + (Mirrors.Count == 0 ? "" : " and mirrored in banks " + string.Join(", ", Mirrors.Select(mirror => mirror.First == mirror.Last
            ? StateValue.Hex(mirror.First, 2)
            : $"{StateValue.Hex(mirror.First, 2)}-{StateValue.Hex(mirror.Last, 2)}")));

    /// <inheritdoc/>
    public bool Equals(Segment? other) =>
        other is not null && Name == other.Name && Size == other.Size && Declaration == other.Declaration
        && DirectPage == other.DirectPage && Bank == other.Bank && Mirrors.SequenceEqual(other.Mirrors);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Size, Declaration, DirectPage, Bank, Mirrors.Count);
}
