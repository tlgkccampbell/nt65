// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.patch @op</c>: the one instruction the store above writes into.</summary>
public sealed class PatchDirectiveSyntax : StatementSyntax
{
    internal PatchDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.patch</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The label of the instruction written to, or null.</summary>
    public NameExpressionSyntax? Target =>
        Green is GreenSyntax ? FirstNode<NameExpressionSyntax>() : SlotNode<NameExpressionSyntax>(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitPatchDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitPatchDirective(this);
}
