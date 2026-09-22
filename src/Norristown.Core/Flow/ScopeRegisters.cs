using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What one inline <c>.scope</c> block of a routine does to the registers: which of them are,
/// wherever the block is left, still what they were where it was entered. A block is a part of
/// a routine and asks the same question of itself that the routine asks of its caller.
/// </summary>
/// <param name="Opener">The line that opens the block, where the answer belongs.</param>
/// <param name="Kept">The registers it hands on unchanged.</param>
/// <param name="Complete">Whether every call it makes was one nt65 could follow.</param>
public readonly record struct ScopeRegisters(TextSpan Opener, Registers Kept, bool Complete);
