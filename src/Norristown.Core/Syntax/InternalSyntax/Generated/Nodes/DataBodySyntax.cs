// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.DataBodySyntax"/>.
/// The <c>{</c> of a data directive whose values are written on the lines it opens.
/// </summary>
internal sealed class DataBodySyntax : DataTailSyntax
{
    private readonly GreenToken openBraceToken;

    internal DataBodySyntax(GreenToken openBraceToken)
        : base(SyntaxKind.DataBody, openBraceToken.FullWidth)
    {
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.DataBodySyntax(tree, parent, this, position);
}
