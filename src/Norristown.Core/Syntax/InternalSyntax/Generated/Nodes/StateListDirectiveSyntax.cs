// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateListDirectiveSyntax"/>.
/// A directive that takes the items of a signature: <c>.state</c> or <c>.ensure</c>.
/// </summary>
internal abstract class StateListDirectiveSyntax : StatementSyntax
{
    private protected StateListDirectiveSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
