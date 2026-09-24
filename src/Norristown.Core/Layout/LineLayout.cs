using Norristown.Processor;

namespace Norristown.Layout;

/// <summary>
/// Describes one line's contribution to the byte stream, which is its length and, for an
/// instruction, the addressing mode chosen for it and the prefix that makes that choice explicit
/// in the output.
/// </summary>
/// <param name="Length">How many bytes the line generates.</param>
/// <param name="Mode">The addressing mode, for an instruction; null for data.</param>
/// <param name="Prefix">The <c>z:</c> or <c>a:</c> to write before the operand, or null.</param>
/// <param name="Inverted">
/// Whether a long branch is emitted as the inverted short branch over a <c>jmp</c>. A branch
/// takes that form when nt65 does not know its target to be in range.
/// </param>
/// <param name="Cycles">
/// How long the instruction takes, or null for data and for an instruction nt65 has no count for.
/// </param>
/// <param name="Causes">
/// Where <paramref name="Cycles"/> is an interval, what the extra cycles at the top of it are
/// spent on, in the order they are spent; empty where the count is exact.
/// </param>
/// <param name="Bits">
/// The width, 8 or 16, of a 65816 immediate whose size follows a register's width; null for
/// every other line, and on the CPUs whose immediates are always a byte.
/// </param>
/// <param name="Ensured">The instructions an <c>.ensure</c> emits; null for every other line.</param>
/// <param name="Slot">
/// The <c>n</c> of <c>n,s</c> that a stack-relative operand naming a frame's member resolves to;
/// null for every other line.
/// </param>
/// <param name="Direct">
/// The offset into the direct page that a <c>d:</c> operand is emitted as, computed from the D
/// the analysis found; null for every other line, and where D is not known.
/// </param>
public sealed record LineLayout(
    int Length, AddressingMode? Mode, string? Prefix, bool Inverted = false, CycleCount? Cycles = null,
    int? Bits = null, Ensured? Ensured = null, int? Slot = null, long? Direct = null,
    IReadOnlyList<string>? Causes = null);

