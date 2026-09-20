// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.MultiProcDeclarationSyntax"/>.
/// <c>.multiproc E, b: signature {</c>: one routine per member of the enum <c>E</c>.
/// </summary>
internal sealed class MultiProcDeclarationSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenNode expression;
    private readonly GreenToken commaToken;
    private readonly GreenToken name;
    private readonly GreenNode? signature;
    private readonly GreenToken openBraceToken;

    internal MultiProcDeclarationSyntax(
        GreenToken keyword,
        GreenNode expression,
        GreenToken commaToken,
        GreenToken name,
        GreenNode? signature,
        GreenToken openBraceToken)
        : base(SyntaxKind.MultiProcDeclaration, keyword.FullWidth + expression.FullWidth + commaToken.FullWidth + name.FullWidth + (signature?.FullWidth ?? 0) + openBraceToken.FullWidth)
    {
        this.keyword = keyword;
        this.expression = expression;
        this.commaToken = commaToken;
        this.name = name;
        this.signature = signature;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.expression,
        2 => this.commaToken,
        3 => this.name,
        4 => this.signature,
        5 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.MultiProcDeclarationSyntax(tree, parent, this, position);
}
