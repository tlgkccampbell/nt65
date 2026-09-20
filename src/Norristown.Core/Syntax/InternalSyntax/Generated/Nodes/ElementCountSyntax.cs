// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ElementCountSyntax"/>.
/// <c>[n]</c>, or <c>[]</c> for as many elements as the values given.
/// </summary>
internal sealed class ElementCountSyntax : GreenNode
{
    private readonly GreenToken openBracketToken;
    private readonly GreenNode? count;
    private readonly GreenToken closeBracketToken;

    internal ElementCountSyntax(
        GreenToken openBracketToken,
        GreenNode? count,
        GreenToken closeBracketToken)
        : base(SyntaxKind.ElementCount, openBracketToken.FullWidth + (count?.FullWidth ?? 0) + closeBracketToken.FullWidth)
    {
        this.openBracketToken = openBracketToken;
        this.count = count;
        this.closeBracketToken = closeBracketToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBracketToken,
        1 => this.count,
        2 => this.closeBracketToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ElementCountSyntax(tree, parent, this, position);
}
