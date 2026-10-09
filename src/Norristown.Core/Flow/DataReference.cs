using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents a statement that takes the address of a <see cref="DataLocation"/> without reaching
/// it, such as <c>ldx #tmp</c>, <c>lda #&lt;ptr</c> or <c>.addr tmp</c> in a table.
/// </summary>
/// <param name="Line">
/// The statement. Inside a macro expansion, it is the outermost call in the statement's file.
/// </param>
/// <param name="Routine">The routine whose instruction takes the address, or null for a statement in no routine, such as a table.</param>
public sealed record DataReference(SyntaxNode Line, Symbol? Routine);
