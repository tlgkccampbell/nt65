// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>inline .strz</c>: the data after each call is a zero-terminated string.</summary>
public sealed class StateInlineItemSyntax : StateItemSyntax
{
    internal StateInlineItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>inline</c>.</summary>
    public new SyntaxToken Name => SlotToken(0);

    /// <summary>The <c>.strz</c>.</summary>
    public new SyntaxToken StrzToken => SlotToken(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateInlineItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateInlineItem(this);
}
