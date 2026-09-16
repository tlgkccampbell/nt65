namespace Norristown.Layout;

/// <summary>
/// What one line contributes to the byte stream: its length, and for an instruction the
/// addressing mode chosen for it (§7.2) and the prefix that makes that choice explicit in
/// the output (§13).
/// </summary>
/// <param name="Length">How many bytes the line generates.</param>
/// <param name="Mode">The addressing mode, for an instruction; null for data.</param>
/// <param name="Prefix">The <c>z:</c> or <c>a:</c> to write before the operand, or null.</param>
public sealed record LineLayout(int Length, AddressingMode? Mode, string? Prefix);
