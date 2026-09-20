// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.EachDirectiveSyntax"/>.
/// <c>.each what, name {</c>.
/// </summary>
internal sealed class EachDirectiveSyntax : RepetitionDirectiveSyntax
{
    private readonly GreenToken keyword;
    private readonly ExpressionSyntax expression;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? name;
    private readonly GreenToken openBraceToken;

    internal EachDirectiveSyntax(
        GreenToken keyword,
        ExpressionSyntax expression,
        GreenToken? commaToken,
        GreenToken? name,
        GreenToken openBraceToken)
        : base(SyntaxKind.EachDirective, keyword.FullWidth + expression.FullWidth + (commaToken?.FullWidth ?? 0) + (name?.FullWidth ?? 0) + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.expression = expression;
        this.commaToken = commaToken;
        this.name = name;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.expression,
        2 => this.commaToken,
        3 => this.name,
        4 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.EachDirectiveSyntax(tree, parent, this, position);
}
