using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>Represents one instruction that reaches a location on a <see cref="DirectPage"/>.</summary>
/// <param name="Line">
/// The statement to show for the instruction. Inside a macro expansion, it is the outermost call in
/// the routine's own file.
/// </param>
/// <param name="Reads">Whether the instruction reads the location.</param>
/// <param name="Writes">Whether the instruction writes the location.</param>
/// <param name="Times">
/// How many times one pass through the routine runs the instruction, counting only the loops whose
/// iteration counts nt65 knows. It is 1 outside every counted loop.
/// </param>
/// <param name="InUncountedLoop">
/// Whether the instruction is inside a loop whose iteration count nt65 does not know, so that it
/// may run any number of times more than <paramref name="Times"/> says.
/// </param>
public readonly record struct PageAccess(SyntaxNode Line, bool Reads, bool Writes, long Times, bool InUncountedLoop);
