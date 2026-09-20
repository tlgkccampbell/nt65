// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The name of a signature set, which stands for the items it was declared with.</summary>
public sealed class StateSetItemSyntax : StateItemSyntax
{
    internal StateSetItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The set's name.</summary>
    public new NameExpressionSyntax Name => SlotNode<NameExpressionSyntax>(0);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateSetItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateSetItem(this);
}
