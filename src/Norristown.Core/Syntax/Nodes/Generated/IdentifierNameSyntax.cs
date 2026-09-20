// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One name, and the <c>[i]</c> after it when it has one: a part of a path, a parameter's word, a register.</summary>
public sealed class IdentifierNameSyntax : SyntaxNode
{
    internal IdentifierNameSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The name.</summary>
    public SyntaxToken Name => SlotToken(0);

    /// <summary>The <c>[i]</c> after the name, or null.</summary>
    public ElementIndexSyntax? Index => SlotNodeOrNull<ElementIndexSyntax>(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitIdentifierName(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitIdentifierName(this);
}
