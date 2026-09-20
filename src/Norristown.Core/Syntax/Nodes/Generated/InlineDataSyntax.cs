// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The values of a data directive, written on its own line after the element type.</summary>
public sealed class InlineDataSyntax : DataTailSyntax
{
    internal InlineDataSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The values.</summary>
    public SeparatedSyntaxList<SyntaxNode> Values => SlotSeparatedList<SyntaxNode>(0);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitInlineData(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitInlineData(this);
}
