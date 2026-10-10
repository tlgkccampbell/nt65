using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents one segment of the program, with its name and the address size every symbol in it
/// gets. A segment is declared exactly once, so references to it are sized from this record.
/// </summary>
/// <param name="Name">The segment's name, as it is written between the quotes.</param>
/// <param name="Size">The address size of symbols in the segment.</param>
/// <param name="Declaration">
/// Where the segment was declared, or null for one of the standard names. For a segment that a
/// linked configuration declares, it is the first configuration line that places it.
/// </param>
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
    /// Gets where the project's linked configurations place the segment, one line for each
    /// configuration that has it. It is empty for a program without <c>links</c>.
    /// </summary>
    public IReadOnlyList<Span> Placements { get; init; } = [];

    /// <summary>
    /// Gets the memory areas the project's linked configurations run the segment in, one for each
    /// configuration whose area has a start and a size nt65 can work out. It is empty for a
    /// program without <c>links</c>, and a segment with no known area is never found out of reach.
    /// </summary>
    public IReadOnlyList<RunArea> Runs { get; init; } = [];

    /// <summary>
    /// Gets the memory areas the project's linked configurations load the segment into, one for
    /// each configuration whose area has a start and a size nt65 can work out. It is empty for a
    /// program without <c>links</c>, where nt65 does not know where a segment loads.
    /// </summary>
    public IReadOnlyList<RunArea> Loads { get; init; } = [];

    /// <summary>
    /// Gets the address that the first linked configuration placing the segment gives with
    /// <c>start</c>, or null when it gives none or nt65 cannot work it out.
    /// </summary>
    public long? Start { get; init; }

    /// <summary>
    /// Gets the offset into its memory area that the first linked configuration placing the
    /// segment gives with <c>offset</c>, or null when it gives none or nt65 cannot work it out.
    /// </summary>
    public long? Offset { get; init; }

    /// <summary>
    /// Gets the alignment of the segment's start that the first linked configuration placing the
    /// segment asks for with <c>align</c>, or null when it asks for none or nt65 cannot work it out.
    /// </summary>
    public long? Align { get; init; }

    /// <summary>
    /// Gets a value indicating whether a linked configuration gives the segment
    /// <c>define = yes</c>, so that ld65 defines the symbols <c>.loadof</c>, <c>.runof</c> and
    /// <c>.spanof</c> stand for.
    /// </summary>
    public bool IsDefined { get; init; }

    /// <summary>
    /// Gets why ld65 writes none of the segment's bytes, such as <c>has `type = bss`</c>, or null
    /// when some linked configuration writes them. It is null for a program without <c>links</c>.
    /// </summary>
    public string? Unwritten { get; init; }

    /// <summary>
    /// Gets the byte ld65 fills the segment's padding with, or null when no configuration nt65
    /// reads places the segment. nt65 never writes a linker configuration, so without
    /// <c>links</c> it cannot see the fill, and it does not guess one.
    /// </summary>
    public SegmentFill? Fill { get; init; }

    /// <summary>
    /// Gets where the project file's <c>segments</c> adds to a segment that a linked configuration
    /// declares, or null when it adds nothing.
    /// </summary>
    public Span? Addition { get; init; }

    /// <summary>
    /// Returns a value indicating whether the segment's symbols are reached with the data bank at
    /// <paramref name="bank"/>, which is so when it is the home bank or a mirror.
    /// </summary>
    public bool IsSeenFrom(long bank) =>
        Bank == bank || Mirrors.Any(mirror => bank >= mirror.First && bank <= mirror.Last);

    /// <summary>
    /// Returns the pair of memory areas that keep code in this segment from ever seeing
    /// <paramref name="other"/>, or null when no configuration runs the two in different areas that
    /// cover the same addresses.
    /// </summary>
    public (RunArea Here, RunArea There)? Excluding(Segment other)
    {
        foreach (var here in Runs)
        {
            foreach (var there in other.Runs)
            {
                if (here.Excludes(there))
                    return (here, there);
            }
        }
        return null;
    }

    /// <summary>
    /// Formats the segment's banks for a message, such as <c>in bank $7e</c>, <c>in bank $00 and
    /// mirrored in bank $80</c>, or <c>in bank $7e and mirrored in banks $00-$3f, $80-$bf</c>.
    /// </summary>
    public string FormatBanks() =>
        $"in bank {StateValue.Hex(Bank ?? 0, 2)}" + (Mirrors.Count == 0 ? "" : " and mirrored in " + StateValue.FormatBanks(Mirrors));

    /// <inheritdoc/>
    public bool Equals(Segment? other) =>
        other is not null && Name == other.Name && Size == other.Size && Declaration == other.Declaration
        && DirectPage == other.DirectPage && Bank == other.Bank && Mirrors.SequenceEqual(other.Mirrors)
        && Space == other.Space && Placements.SequenceEqual(other.Placements) && Runs.SequenceEqual(other.Runs)
        && Start == other.Start && Offset == other.Offset && Align == other.Align && IsDefined == other.IsDefined
        && Addition == other.Addition && Fill == other.Fill;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Size, Declaration, DirectPage, Bank, Mirrors.Count);
}
