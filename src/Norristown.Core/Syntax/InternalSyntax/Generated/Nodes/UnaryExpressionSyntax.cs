// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.UnaryExpressionSyntax"/>.
/// <c>op operand</c>.
/// </summary>
internal sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    private readonly GreenToken operatorToken;
    private readonly GreenNode operand;

    internal UnaryExpressionSyntax(
        GreenToken operatorToken,
        GreenNode operand)
        : base(SyntaxKind.UnaryExpression, operatorToken.FullWidth + operand.FullWidth)
    {
        this.operatorToken = operatorToken;
        this.operand = operand;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.operatorToken,
        1 => this.operand,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.UnaryExpressionSyntax(tree, parent, this, position);
}
