using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>Represents one instruction that reaches a location on a <see cref="DirectPage"/>.</summary>
/// <param name="Line">
/// The statement to show for the instruction. Inside a macro expansion, it is the outermost call in
/// the routine's own file.
/// </param>
/// <param name="Reads">Whether the instruction reads the location.</param>
/// <param name="Writes">Whether the instruction writes the location.</param>
/// <param name="InLoop">Whether the instruction is inside a loop of its routine.</param>
public readonly record struct PageAccess(SyntaxNode Line, bool Reads, bool Writes, bool InLoop);
