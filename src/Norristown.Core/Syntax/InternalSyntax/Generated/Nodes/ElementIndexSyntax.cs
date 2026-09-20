// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ElementIndexSyntax"/>.
/// <c>[i]</c> after a name: which element of a counted declaration it stands for.
/// </summary>
internal sealed class ElementIndexSyntax : GreenNode
{
    private readonly GreenToken openBracketToken;
    private readonly GreenNode index;
    private readonly GreenToken closeBracketToken;

    internal ElementIndexSyntax(
        GreenToken openBracketToken,
        GreenNode index,
        GreenToken closeBracketToken)
        : base(SyntaxKind.ElementIndex, openBracketToken.FullWidth + index.FullWidth + closeBracketToken.FullWidth)
    {
        this.openBracketToken = openBracketToken;
        this.index = index;
        this.closeBracketToken = closeBracketToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBracketToken,
        1 => this.index,
        2 => this.closeBracketToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ElementIndexSyntax(tree, parent, this, position);
}
