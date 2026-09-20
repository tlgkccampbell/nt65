// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ConditionalDirectiveSyntax"/>.
/// A line that opens a branch with a condition: <c>.if expr {</c> or <c>} .elseif expr {</c>.
/// </summary>
internal abstract class ConditionalDirectiveSyntax : StatementSyntax
{
    private protected ConditionalDirectiveSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
