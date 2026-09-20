// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ImportItemSyntax"/>.
/// <c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.
/// </summary>
internal sealed class ImportItemSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken? equalsToken;
    private readonly GreenNode? value;
    private readonly GreenToken? colonToken;
    private readonly GreenToken? addressSize;
    private readonly GreenNode? signature;

    internal ImportItemSyntax(
        GreenToken name,
        GreenToken? equalsToken,
        GreenNode? value,
        GreenToken? colonToken,
        GreenToken? addressSize,
        GreenNode? signature)
        : base(SyntaxKind.ImportItem, name.FullWidth + (equalsToken?.FullWidth ?? 0) + (value?.FullWidth ?? 0) + (colonToken?.FullWidth ?? 0) + (addressSize?.FullWidth ?? 0) + (signature?.FullWidth ?? 0))
    {
        this.name = name;
        this.equalsToken = equalsToken;
        this.value = value;
        this.colonToken = colonToken;
        this.addressSize = addressSize;
        this.signature = signature;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.equalsToken,
        2 => this.value,
        3 => this.colonToken,
        4 => this.addressSize,
        5 => this.signature,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ImportItemSyntax(tree, parent, this, position);
}
