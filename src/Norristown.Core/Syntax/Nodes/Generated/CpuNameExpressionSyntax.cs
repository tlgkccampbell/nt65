// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A CPU's name, where an expression is read.</summary>
public sealed class CpuNameExpressionSyntax : LiteralExpressionSyntax
{
    internal CpuNameExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Token => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitCpuNameExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitCpuNameExpression(this);
}
