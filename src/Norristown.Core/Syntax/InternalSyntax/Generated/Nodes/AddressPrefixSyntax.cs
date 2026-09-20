// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.AddressPrefixSyntax"/>.
/// <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c> before an address.
/// </summary>
internal sealed class AddressPrefixSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken colonToken;

    internal AddressPrefixSyntax(
        GreenToken name,
        GreenToken colonToken)
        : base(SyntaxKind.AddressPrefix, name.FullWidth + colonToken.FullWidth)
    {
        this.name = name;
        this.colonToken = colonToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.colonToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.AddressPrefixSyntax(tree, parent, this, position);
}
