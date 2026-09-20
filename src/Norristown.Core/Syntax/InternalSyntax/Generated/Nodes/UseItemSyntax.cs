// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.UseItemSyntax"/>.
/// One name in the braces of a <c>.use</c>, and the name it is brought in as.
/// </summary>
internal sealed class UseItemSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken? asKeyword;
    private readonly GreenToken? alias;

    internal UseItemSyntax(
        GreenToken name,
        GreenToken? asKeyword,
        GreenToken? alias)
        : base(SyntaxKind.UseItem, name.FullWidth + (asKeyword?.FullWidth ?? 0) + (alias?.FullWidth ?? 0))
    {
        this.name = name;
        this.asKeyword = asKeyword;
        this.alias = alias;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.asKeyword,
        2 => this.alias,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.UseItemSyntax(tree, parent, this, position);
}
