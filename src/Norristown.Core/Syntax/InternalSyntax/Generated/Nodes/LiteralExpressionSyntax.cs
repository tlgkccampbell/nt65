// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.LiteralExpressionSyntax"/>.
/// An expression that is one token.
/// </summary>
internal abstract class LiteralExpressionSyntax : ExpressionSyntax
{
    private protected LiteralExpressionSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
