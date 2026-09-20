// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateDirectiveSyntax"/>.
/// <c>.state a16, i8</c>: the items of a signature, asserted and set at one point.
/// </summary>
internal sealed class StateDirectiveSyntax : StateListDirectiveSyntax
{
    private readonly GreenToken keyword;
    private readonly StateListSyntax items;

    internal StateDirectiveSyntax(
        GreenToken keyword,
        StateListSyntax items)
        : base(SyntaxKind.StateDirective, keyword.FullWidth + items.FullWidth)
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
        new Red.StateDirectiveSyntax(tree, parent, this, position);
}
