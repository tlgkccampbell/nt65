using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents what one inline <c>.scope</c> block of a routine does to the registers. It records
/// which registers still hold, at every exit from the block, the values they had when the block
/// was entered. This is the question asked of a whole routine, applied to one part of it.
/// </summary>
/// <param name="Opener">The line that opens the block, to which the answer belongs.</param>
/// <param name="Kept">The registers the block passes on unchanged.</param>
/// <param name="Complete">Whether every call it makes was one nt65 could follow.</param>
public readonly record struct ScopeRegisters(TextSpan Opener, Registers Kept, bool Complete);
