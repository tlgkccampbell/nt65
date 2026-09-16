namespace Norristown.Project;

/// <summary>
/// One entry of the project's <c>ranges</c>: a range of absolute addresses, and the banks a
/// constant address in it may be reached from. It is for hardware registers that are mirrored
/// in some banks only, where <c>sta $2100</c> with the data bank at <c>$7e</c> writes RAM.
/// </summary>
/// <param name="First">The lowest address in the range.</param>
/// <param name="Last">The highest address in the range.</param>
/// <param name="Banks">The banks it may be reached from, each a range of banks.</param>
public sealed record AccessRange(long First, long Last, IReadOnlyList<(long First, long Last)> Banks)
{
    /// <summary>Whether <paramref name="address"/> is in the range.</summary>
    public bool Covers(long address) => address >= First && address <= Last;

    /// <summary>Whether the range may be reached with the data bank at <paramref name="bank"/>.</summary>
    public bool Permits(long bank) => Banks.Any(banks => bank >= banks.First && bank <= banks.Last);

    /// <summary>The banks as the project file writes them, for a message: <c>$00-$3f, $80-$bf</c>.</summary>
    public string SpellBanks() => string.Join(", ", Banks.Select(banks => banks.First == banks.Last
        ? Semantics.StateValue.Hex(banks.First, 2)
        : $"{Semantics.StateValue.Hex(banks.First, 2)}-{Semantics.StateValue.Hex(banks.Last, 2)}"));
}
