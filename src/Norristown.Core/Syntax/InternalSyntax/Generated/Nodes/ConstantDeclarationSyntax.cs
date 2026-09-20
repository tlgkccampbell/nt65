// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ConstantDeclarationSyntax"/>.
/// <c>name = expr</c>.
/// </summary>
internal sealed class ConstantDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken name;
    private readonly GreenToken equalsToken;
    private readonly ExpressionSyntax value;

    internal ConstantDeclarationSyntax(
        GreenToken name,
        GreenToken equalsToken,
        ExpressionSyntax value)
        : base(SyntaxKind.ConstantDeclaration, name.FullWidth + equalsToken.FullWidth + value.FullWidth)
    {
        this.name = name;
        this.equalsToken = equalsToken;
        this.value = value;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.equalsToken,
        2 => this.value,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ConstantDeclarationSyntax(tree, parent, this, position);
}
