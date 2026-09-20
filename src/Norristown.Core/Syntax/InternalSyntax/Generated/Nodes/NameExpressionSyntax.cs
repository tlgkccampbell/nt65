// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.NameExpressionSyntax"/>.
/// A name: <c>label</c>, <c>@local</c>, <c>gfx::init</c>, <c>::top_level</c>, <c>table[2]::x</c>.
/// </summary>
internal sealed class NameExpressionSyntax : ExpressionSyntax
{
    private readonly GreenToken? globalToken;
    private readonly GreenSeparatedList? parts;

    internal NameExpressionSyntax(
        GreenToken? globalToken,
        GreenSeparatedList? parts)
        : base(SyntaxKind.NameExpression, (globalToken?.FullWidth ?? 0) + (parts?.FullWidth ?? 0))
    {
        this.globalToken = globalToken;
        this.parts = parts;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.globalToken,
        1 => this.parts,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.NameExpressionSyntax(tree, parent, this, position);
}
