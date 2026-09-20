// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BracedOperandSyntax"/>.
/// <c>{buf,x}</c>: a whole operand as a macro argument.
/// </summary>
internal sealed class BracedOperandSyntax : GreenNode
{
    private readonly GreenToken openBraceToken;
    private readonly GreenNode operand;
    private readonly GreenToken closeBraceToken;

    internal BracedOperandSyntax(
        GreenToken openBraceToken,
        GreenNode operand,
        GreenToken closeBraceToken)
        : base(SyntaxKind.BracedOperand, openBraceToken.FullWidth + operand.FullWidth + closeBraceToken.FullWidth)
    {
        this.openBraceToken = openBraceToken;
        this.operand = operand;
        this.closeBraceToken = closeBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBraceToken,
        1 => this.operand,
        2 => this.closeBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BracedOperandSyntax(tree, parent, this, position);
}
