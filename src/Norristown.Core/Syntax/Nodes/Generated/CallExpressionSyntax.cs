// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name(args)</c> or <c>.function(args)</c>.</summary>
public sealed class CallExpressionSyntax : ExpressionSyntax
{
    internal CallExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.func</c> called, or null for a built-in function.</summary>
    public NameExpressionSyntax? Callee =>
        Green is GreenSyntax ? ChildNodes[0] as NameExpressionSyntax : SlotNodeOrNull<NameExpressionSyntax>(0);

    /// <summary>The built-in function called, or null for a <c>.func</c>.</summary>
    public SyntaxToken? Function =>
        Green is GreenSyntax ? TokenAt(0) is { Kind: SyntaxKind.Directive } function ? function : null : SlotTokenOrNull(1);

    /// <summary>The arguments.</summary>
    public ArgumentListSyntax Arguments =>
        Green is GreenSyntax ? FirstNode<ArgumentListSyntax>()! : SlotNode<ArgumentListSyntax>(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitCallExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitCallExpression(this);
}
