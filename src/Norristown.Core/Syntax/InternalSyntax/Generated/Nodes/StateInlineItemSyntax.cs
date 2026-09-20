// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateInlineItemSyntax"/>.
/// <c>inline .strz</c>: the data after each call is a zero-terminated string.
/// </summary>
internal sealed class StateInlineItemSyntax : StateItemSyntax
{
    private readonly GreenToken name;
    private readonly GreenToken strzToken;

    internal StateInlineItemSyntax(
        GreenToken name,
        GreenToken strzToken)
        : base(SyntaxKind.StateInlineItem, name.FullWidth + strzToken.FullWidth)
    {
        this.name = name;
        this.strzToken = strzToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.strzToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateInlineItemSyntax(tree, parent, this, position);
}
