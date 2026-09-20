// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.IfDirectiveSyntax"/>.
/// <c>.if expr {</c>.
/// </summary>
internal sealed class IfDirectiveSyntax : ConditionalDirectiveSyntax
{
    private readonly GreenToken keyword;
    private readonly ExpressionSyntax condition;
    private readonly GreenToken openBraceToken;

    internal IfDirectiveSyntax(
        GreenToken keyword,
        ExpressionSyntax condition,
        GreenToken openBraceToken)
        : base(SyntaxKind.IfDirective, keyword.FullWidth + condition.FullWidth + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.condition = condition;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.condition,
        2 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.IfDirectiveSyntax(tree, parent, this, position);
}
