// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BinaryExpressionSyntax"/>.
/// <c>left op right</c>.
/// </summary>
internal sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    private readonly GreenNode left;
    private readonly GreenToken operatorToken;
    private readonly GreenNode right;

    internal BinaryExpressionSyntax(
        GreenNode left,
        GreenToken operatorToken,
        GreenNode right)
        : base(SyntaxKind.BinaryExpression, left.FullWidth + operatorToken.FullWidth + right.FullWidth)
    {
        this.left = left;
        this.operatorToken = operatorToken;
        this.right = right;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.left,
        1 => this.operatorToken,
        2 => this.right,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BinaryExpressionSyntax(tree, parent, this, position);
}
