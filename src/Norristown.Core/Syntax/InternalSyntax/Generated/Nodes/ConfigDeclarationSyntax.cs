// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ConfigDeclarationSyntax"/>.
/// <c>.config NAME = value</c>: a setting, whose value the build may give instead.
/// </summary>
internal sealed class ConfigDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken equalsToken;
    private readonly ExpressionSyntax value;

    internal ConfigDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken equalsToken,
        ExpressionSyntax value)
        : base(SyntaxKind.ConfigDeclaration, keyword.FullWidth + name.FullWidth + equalsToken.FullWidth + value.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
        this.equalsToken = equalsToken;
        this.value = value;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.equalsToken,
        3 => this.value,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ConfigDeclarationSyntax(tree, parent, this, position);
}
