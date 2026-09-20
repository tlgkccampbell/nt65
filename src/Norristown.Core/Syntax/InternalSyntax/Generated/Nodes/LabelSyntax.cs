// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.LabelSyntax"/>.
/// <c>name:</c> at the start of a line.
/// </summary>
internal sealed class LabelSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken colonToken;

    internal LabelSyntax(
        GreenToken name,
        GreenToken colonToken)
        : base(SyntaxKind.Label, name.FullWidth + colonToken.FullWidth)
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
        new Red.LabelSyntax(tree, parent, this, position);
}
