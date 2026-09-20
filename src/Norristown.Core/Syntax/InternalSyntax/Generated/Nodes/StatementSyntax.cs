// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StatementSyntax"/>.
/// What a line's tokens parse to: a declaration, a directive, an instruction, a member of a body.
/// A few of them are also written inside another statement, as an instruction is after a label
/// and a data directive after <c>.data name:</c>. A statement is only its own tokens wherever it
/// is written: the line break, whatever the statement could not take and the <c>.export</c>
/// before a declaration all belong to the line (<see cref="LineSyntax"/>).
/// </summary>
internal abstract class StatementSyntax : GreenNode
{
    private protected StatementSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
