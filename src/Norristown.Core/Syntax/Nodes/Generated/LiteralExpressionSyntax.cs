// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>An expression that is one token.</summary>
public abstract class LiteralExpressionSyntax : ExpressionSyntax
{
    private protected LiteralExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The token the expression is.</summary>
    public abstract SyntaxToken Token { get; }
}
