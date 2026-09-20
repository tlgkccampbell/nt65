// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ErrorExpressionSyntax"/>.
/// Where an expression was expected and none could be read.
/// </summary>
internal sealed class ErrorExpressionSyntax : ExpressionSyntax
{
    private readonly GreenToken? token;

    internal ErrorExpressionSyntax(
        GreenToken? token)
        : base(SyntaxKind.ErrorExpression, (token?.FullWidth ?? 0))
    {
        this.token = token;
    }

    /// <inheritdoc/>
    public override bool IsMissing => true;

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.token,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ErrorExpressionSyntax(tree, parent, this, position);
}
