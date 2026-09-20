// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SignatureDeclarationSyntax"/>.
/// <c>.signature std = a8, i16, dp = 0</c>: a name for items a signature uses.
/// </summary>
internal sealed class SignatureDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken equalsToken;
    private readonly StateListSyntax items;

    internal SignatureDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken equalsToken,
        StateListSyntax items)
        : base(SyntaxKind.SignatureDeclaration, keyword.FullWidth + name.FullWidth + equalsToken.FullWidth + items.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.equalsToken = equalsToken;
        this.items = items;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.equalsToken,
        3 => this.items,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.SignatureDeclarationSyntax(tree, parent, this, position);
}
