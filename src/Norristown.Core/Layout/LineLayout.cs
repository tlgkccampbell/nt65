using Norristown.Processor;

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
/// <param name="Causes">
/// What the top of <paramref name="Cycles"/> is paid for where the count is an interval, in
/// the order it is paid; empty where the count is exact.
/// </param>
/// <param name="Bits">
/// How wide a 65816 immediate that follows a register's width is, 8 or 16; null for every
/// other line, and on the CPUs whose immediates are always a byte.
/// </param>
/// <param name="Ensured">What an <c>.ensure</c> writes; null for every other line.</param>
/// <param name="Slot">
/// The <c>n</c> of <c>n,s</c> a stack-relative operand naming a frame's member comes to; null
/// for every other line.
/// </param>
/// <param name="Direct">
/// The offset into the direct page a <c>d:</c> operand is written as, from the D the analysis
/// found; null for every other line, and where D is not known.
/// </param>
public sealed record LineLayout(
    int Length, AddressingMode? Mode, string? Prefix, bool Inverted = false, CycleCount? Cycles = null,
    int? Bits = null, Ensured? Ensured = null, int? Slot = null, long? Direct = null,
    IReadOnlyList<string>? Causes = null);

