// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ParenthesizedExpressionSyntax"/>.
/// <c>(expr)</c>.
/// </summary>
internal sealed class ParenthesizedExpressionSyntax : ExpressionSyntax
{
    private readonly GreenToken openParenToken;
    private readonly GreenNode expression;
    private readonly GreenToken closeParenToken;

    internal ParenthesizedExpressionSyntax(
        GreenToken openParenToken,
        GreenNode expression,
        GreenToken closeParenToken)
        : base(SyntaxKind.ParenthesizedExpression, openParenToken.FullWidth + expression.FullWidth + closeParenToken.FullWidth)
    {
        this.openParenToken = openParenToken;
        this.expression = expression;
        this.closeParenToken = closeParenToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openParenToken,
        1 => this.expression,
        2 => this.closeParenToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ParenthesizedExpressionSyntax(tree, parent, this, position);
}
