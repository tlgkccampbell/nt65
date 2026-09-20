// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Where an expression was expected and none could be read.</summary>
public sealed class ErrorExpressionSyntax : ExpressionSyntax
{
    internal ErrorExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The directive that was read as far as it went, or null.</summary>
    public SyntaxToken? Token => Green is GreenSyntax ? TokenAt(0) : SlotTokenOrNull(0);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitErrorExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitErrorExpression(this);
}
