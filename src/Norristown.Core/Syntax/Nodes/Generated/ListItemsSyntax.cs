// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One line of a <c>.list</c>, which holds one or more comma-separated items.</summary>
public sealed class ListItemsSyntax : StatementSyntax
{
    private ImmutableArray<ExpressionSyntax> items;

    internal ListItemsSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The items.</summary>
    public ImmutableArray<ExpressionSyntax> Items => Nodes(ref items);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitListItems(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitListItems(this);
}
