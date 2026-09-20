// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A state word and the value it is given: <c>dp = $2100</c>, <c>args 2</c>, <c>inline 4</c>.</summary>
public sealed class StateValueItemSyntax : StateItemSyntax
{
    internal StateValueItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The item's word.</summary>
    public new SyntaxToken Name => SlotToken(0);

    /// <summary>The <c>=</c> before the value, or null where the word takes one without.</summary>
    public SyntaxToken? EqualsToken => SlotTokenOrNull(1);

    /// <summary>The value.</summary>
    public new ExpressionSyntax Value => SlotNode<ExpressionSyntax>(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateValueItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateValueItem(this);
}
