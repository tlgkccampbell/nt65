// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.EmptyBlockSyntax"/>.
/// <c>{}</c>: the default of a block parameter a call may leave out.
/// </summary>
internal sealed class EmptyBlockSyntax : GreenNode
{
    private readonly GreenToken openBraceToken;
    private readonly GreenToken closeBraceToken;

    internal EmptyBlockSyntax(
        GreenToken openBraceToken,
        GreenToken closeBraceToken)
        : base(SyntaxKind.EmptyBlock, openBraceToken.FullWidth + closeBraceToken.FullWidth)
    {
        this.openBraceToken = openBraceToken;
        this.closeBraceToken = closeBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBraceToken,
        1 => this.closeBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.EmptyBlockSyntax(tree, parent, this, position);
}
