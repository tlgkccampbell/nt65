// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.CurrentAddressExpressionSyntax"/>.
/// <c>*</c>: the address of the statement it is written in.
/// </summary>
internal sealed class CurrentAddressExpressionSyntax : ExpressionSyntax
{
    private readonly GreenToken starToken;

    internal CurrentAddressExpressionSyntax(
        GreenToken starToken)
        : base(SyntaxKind.CurrentAddressExpression, starToken.FullWidth)
    {
        this.starToken = starToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.starToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.CurrentAddressExpressionSyntax(tree, parent, this, position);
}
