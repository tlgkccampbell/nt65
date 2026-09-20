// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BlockCloseLineSyntax"/>.
/// A <c>}</c> on its own line.
/// </summary>
internal sealed class BlockCloseLineSyntax : StatementSyntax
{
    private readonly GreenToken closeBraceToken;

    internal BlockCloseLineSyntax(
        GreenToken closeBraceToken)
        : base(SyntaxKind.BlockCloseLine, closeBraceToken.FullWidth)
    {
        this.closeBraceToken = closeBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.closeBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BlockCloseLineSyntax(tree, parent, this, position);
}
