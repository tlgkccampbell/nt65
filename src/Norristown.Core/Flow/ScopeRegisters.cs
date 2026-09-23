using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What one inline <c>.scope</c> block of a routine does to the registers: which of them still
/// hold, wherever the block is left, the values they had when it was entered. This is the
/// question asked of a whole routine, applied to one part of it.
/// </summary>
/// <param name="Opener">The line that opens the block, where the answer belongs.</param>
/// <param name="Kept">The registers it hands on unchanged.</param>
/// <param name="Complete">Whether every call it makes was one nt65 could follow.</param>
public readonly record struct ScopeRegisters(TextSpan Opener, Registers Kept, bool Complete);
