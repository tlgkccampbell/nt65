using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents one segment of the program, with its name and the address size every symbol in it
/// gets. A segment is declared exactly once, so references to it are sized from this record.
/// </summary>
/// <param name="Name">The segment's name, as it is written between the quotes.</param>
/// <param name="Size">The address size of symbols in the segment.</param>
/// <param name="Declaration">Where the segment was declared, or null for one of the standard names.</param>
/// <param name="DirectPage">
/// The value of the segment's <c>dp = e</c>, which is the direct page its symbols are meant to be
/// reached through on the 65816. Null when it declares none, in which case nothing about D is
/// checked against it.
/// </param>
/// <param name="Bank">
/// The value of the segment's <c>bank = e</c>, which is its home bank on the 65816: where it lives
/// and where code in it is assumed to run. Null when it declares none.
/// </param>
public sealed record Segment(string Name, AddressSize Size, Span? Declaration, long? DirectPage = null, long? Bank = null)
{
    /// <summary>
    /// Gets the bank ranges the segment's <c>mirrors</c> declares. These are other banks in which
    /// the same memory is visible, such as low WRAM in banks <c>$00-$3f</c>. An absolute operand
    /// reaches the segment's symbols when the data bank is the home bank or any of these.
    /// </summary>
    public IReadOnlyList<(long First, long Last)> Mirrors { get; init; } = [];

    /// <summary>
    /// Gets the address space the segment's <c>space = name</c> puts it in, or null for the
    /// host's.
    /// </summary>
    public string? Space { get; init; }

    /// <summary>
    /// Returns a value indicating whether the segment's symbols are reached with the data bank at
    /// <paramref name="bank"/>, which is so when it is the home bank or a mirror.
    /// </summary>
    public bool IsSeenFrom(long bank) =>
        Bank == bank || Mirrors.Any(mirror => bank >= mirror.First && bank <= mirror.Last);

    /// <summary>
    /// Formats the segment's banks for a message, such as <c>in bank $7e</c>, or <c>in bank $7e
    /// and mirrored in banks $00-$3f, $80-$bf</c>.
    /// </summary>
    public string FormatBanks() =>
        $"in bank {StateValue.Hex(Bank ?? 0, 2)}"
        + (Mirrors.Count == 0 ? "" : " and mirrored in banks " + string.Join(", ", Mirrors.Select(mirror => mirror.First == mirror.Last
            ? StateValue.Hex(mirror.First, 2)
            : $"{StateValue.Hex(mirror.First, 2)}-{StateValue.Hex(mirror.Last, 2)}")));

    /// <inheritdoc/>
    public bool Equals(Segment? other) =>
        other is not null && Name == other.Name && Size == other.Size && Declaration == other.Declaration
        && DirectPage == other.DirectPage && Bank == other.Bank && Mirrors.SequenceEqual(other.Mirrors)
        && Space == other.Space;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Size, Declaration, DirectPage, Bank, Mirrors.Count);
}
