// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.DataDeclarationSyntax"/>.
/// <c>.data name: element</c>, or <c>.data name {</c> for mixed data.
/// </summary>
internal sealed class DataDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken? colonToken;
    private readonly GreenNode? directive;
    private readonly GreenToken? openBraceToken;

    internal DataDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken? colonToken,
        GreenNode? directive,
        GreenToken? openBraceToken)
        : base(SyntaxKind.DataDeclaration, keyword.FullWidth + name.FullWidth + (colonToken?.FullWidth ?? 0) + (directive?.FullWidth ?? 0) + (openBraceToken?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.name = name;
        this.colonToken = colonToken;
        this.directive = directive;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.colonToken,
        3 => this.directive,
        4 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.DataDeclarationSyntax(tree, parent, this, position);
}
