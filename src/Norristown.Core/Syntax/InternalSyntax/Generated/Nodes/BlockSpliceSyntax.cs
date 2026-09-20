// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BlockSpliceSyntax"/>.
/// A name on its own line, which splices a <c>block</c> parameter.
/// </summary>
internal sealed class BlockSpliceSyntax : StatementSyntax
{
    private readonly GreenToken name;

    internal BlockSpliceSyntax(GreenToken name)
        : base(SyntaxKind.BlockSplice, name.FullWidth)
    {
        this.name = name;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BlockSpliceSyntax(tree, parent, this, position);
}
