namespace Norristown.Layout;

/// <summary>
/// What one line contributes to the byte stream: its length, and for an instruction the
/// addressing mode chosen for it and the prefix that makes that choice explicit in
/// the output.
/// </summary>
/// <param name="Length">How many bytes the line generates.</param>
/// <param name="Mode">The addressing mode, for an instruction; null for data.</param>
/// <param name="Prefix">The <c>z:</c> or <c>a:</c> to write before the operand, or null.</param>
/// <param name="Inverted">
/// Whether a long branch is written as the inverted short branch over a <c>jmp</c>, which is
/// the form it takes when nt65 does not know the target to be in range.
/// </param>
/// <param name="Cycles">How long the instruction takes, or null for data and for one nt65 has no count for.</param>
public sealed record LineLayout(
    int Length, AddressingMode? Mode, string? Prefix, bool Inverted = false, CycleCount? Cycles = null);
