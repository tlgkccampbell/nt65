// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ExpressionSyntax"/>.
/// An expression.
/// </summary>
internal abstract class ExpressionSyntax : GreenNode
{
    private protected ExpressionSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }
}
