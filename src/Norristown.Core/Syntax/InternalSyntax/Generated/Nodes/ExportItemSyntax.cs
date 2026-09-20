// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ExportItemSyntax"/>.
/// <c>name</c> or <c>outer::inner</c>, then <c>: size</c> or <c>as "linker_name"</c>.
/// </summary>
internal sealed class ExportItemSyntax : GreenNode
{
    private readonly GreenNode name;
    private readonly GreenToken? colonToken;
    private readonly GreenToken? addressSize;
    private readonly GreenToken? asKeyword;
    private readonly GreenToken? linkerName;

    internal ExportItemSyntax(
        GreenNode name,
        GreenToken? colonToken,
        GreenToken? addressSize,
        GreenToken? asKeyword,
        GreenToken? linkerName)
        : base(SyntaxKind.ExportItem, name.FullWidth + (colonToken?.FullWidth ?? 0) + (addressSize?.FullWidth ?? 0) + (asKeyword?.FullWidth ?? 0) + (linkerName?.FullWidth ?? 0))
    {
        this.name = name;
        this.colonToken = colonToken;
        this.addressSize = addressSize;
        this.asKeyword = asKeyword;
        this.linkerName = linkerName;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.colonToken,
        2 => this.addressSize,
        3 => this.asKeyword,
        4 => this.linkerName,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ExportItemSyntax(tree, parent, this, position);
}
