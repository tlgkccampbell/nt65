// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateValueItemSyntax"/>.
/// A state word and the value it is given: <c>dp = $2100</c>, <c>args 2</c>, <c>inline 4</c>.
/// </summary>
internal sealed class StateValueItemSyntax : StateItemSyntax
{
    private readonly GreenToken name;
    private readonly GreenToken? equalsToken;
    private readonly GreenNode value;

    internal StateValueItemSyntax(
        GreenToken name,
        GreenToken? equalsToken,
        GreenNode value)
        : base(SyntaxKind.StateValueItem, name.FullWidth + (equalsToken?.FullWidth ?? 0) + value.FullWidth)
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
        new Red.StateValueItemSyntax(tree, parent, this, position);
}
