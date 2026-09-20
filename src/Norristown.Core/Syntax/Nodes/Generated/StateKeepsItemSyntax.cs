// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>keeps a, x</c>: the registers a routine leaves as it found them.</summary>
public sealed class StateKeepsItemSyntax : StateItemSyntax
{
    internal StateKeepsItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>keeps</c>.</summary>
    public new SyntaxToken Name => SlotToken(0);

    /// <summary>The registers it keeps.</summary>
    public new SeparatedSyntaxList<IdentifierNameSyntax> Registers => SlotSeparatedList<IdentifierNameSyntax>(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateKeepsItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateKeepsItem(this);
}
