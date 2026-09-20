// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ElseDirectiveSyntax"/>.
/// <c>} .else {</c>.
/// </summary>
internal sealed class ElseDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken closeBraceToken;
    private readonly GreenToken keyword;
    private readonly GreenToken openBraceToken;

    internal ElseDirectiveSyntax(
        GreenToken closeBraceToken,
        GreenToken keyword,
        GreenToken openBraceToken)
        : base(SyntaxKind.ElseDirective, closeBraceToken.FullWidth + keyword.FullWidth + openBraceToken.FullWidth)
    {
        this.closeBraceToken = closeBraceToken;
        this.keyword = keyword;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.closeBraceToken,
        1 => this.keyword,
        2 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ElseDirectiveSyntax(tree, parent, this, position);
}
