namespace Norristown.Project;

/// <summary>
/// Represents one entry of the project's <c>ranges</c>, which is a range of absolute addresses
/// and the banks from which a constant address in it may be reached. It is meant for hardware
/// registers that are mirrored only in some banks, where, for example, <c>sta $2100</c> with the
/// data bank at <c>$7e</c> writes RAM.
/// </summary>
/// <param name="First">The lowest address in the range.</param>
/// <param name="Last">The highest address in the range.</param>
/// <param name="Banks">The banks from which the range may be reached, each given as a range of banks.</param>
public sealed record AccessRange(long First, long Last, IReadOnlyList<(long First, long Last)> Banks)
{
    /// <summary>Returns a value indicating whether <paramref name="address"/> is in the range.</summary>
    public bool Covers(long address) => address >= First && address <= Last;

    /// <summary>
    /// Returns a value indicating whether the range may be reached with the data bank at
    /// <paramref name="bank"/>.
    /// </summary>
    public bool Permits(long bank) => Banks.Any(banks => bank >= banks.First && bank <= banks.Last);

    /// <summary>
    /// Formats the banks for a message in the project file's notation, such as
    /// <c>$00-$3f, $80-$bf</c>.
    /// </summary>
    public string FormatBanks() => string.Join(", ", Banks.Select(banks => banks.First == banks.Last
        ? Semantics.StateValue.Hex(banks.First, 2)
        : $"{Semantics.StateValue.Hex(banks.First, 2)}-{Semantics.StateValue.Hex(banks.Last, 2)}"));
}
