// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.EnsureDirectiveSyntax"/>.
/// <c>.ensure a16, i8</c>: the widths to make hold.
/// </summary>
internal sealed class EnsureDirectiveSyntax : StateListDirectiveSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenNode items;

    internal EnsureDirectiveSyntax(
        GreenToken keyword,
        GreenNode items)
        : base(SyntaxKind.EnsureDirective, keyword.FullWidth + items.FullWidth)
    {
        this.keyword = keyword;
        this.items = items;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.items,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.EnsureDirectiveSyntax(tree, parent, this, position);
}
