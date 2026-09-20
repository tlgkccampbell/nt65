// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.UnionDeclarationSyntax"/>.
/// <c>.union name {</c>.
/// </summary>
internal sealed class UnionDeclarationSyntax : TypeDeclarationSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken? name;
    private readonly GreenToken openBraceToken;

    internal UnionDeclarationSyntax(
        GreenToken keyword,
        GreenToken? name,
        GreenToken openBraceToken)
        : base(SyntaxKind.UnionDeclaration, keyword.FullWidth + (name?.FullWidth ?? 0) + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.UnionDeclarationSyntax(tree, parent, this, position);
}
