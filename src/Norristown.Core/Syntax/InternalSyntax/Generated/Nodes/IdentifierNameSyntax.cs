// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.IdentifierNameSyntax"/>.
/// One name, and the <c>[i]</c> after it when it has one: a part of a path, a parameter's word, a register.
/// </summary>
internal sealed class IdentifierNameSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenNode? index;

    internal IdentifierNameSyntax(
        GreenToken name,
        GreenNode? index)
        : base(SyntaxKind.IdentifierName, name.FullWidth + (index?.FullWidth ?? 0))
    {
        this.name = name;
        this.index = index;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.index,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.IdentifierNameSyntax(tree, parent, this, position);
}
