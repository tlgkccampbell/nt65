// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ElseIfDirectiveSyntax"/>.
/// <c>} .elseif expr {</c>.
/// </summary>
internal sealed class ElseIfDirectiveSyntax : ConditionalDirectiveSyntax
{
    private readonly GreenToken closeBraceToken;
    private readonly GreenToken keyword;
    private readonly GreenNode condition;
    private readonly GreenToken openBraceToken;

    internal ElseIfDirectiveSyntax(
        GreenToken closeBraceToken,
        GreenToken keyword,
        GreenNode condition,
        GreenToken openBraceToken)
        : base(SyntaxKind.ElseIfDirective, closeBraceToken.FullWidth + keyword.FullWidth + condition.FullWidth + openBraceToken.FullWidth)
    {
        this.closeBraceToken = closeBraceToken;
        this.keyword = keyword;
        this.condition = condition;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.closeBraceToken,
        1 => this.keyword,
        2 => this.condition,
        3 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ElseIfDirectiveSyntax(tree, parent, this, position);
}
