// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.UseDirectiveSyntax"/>.
/// <c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or <c>.use a::b as c</c>.
/// </summary>
internal sealed class UseDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly NameExpressionSyntax path;
    private readonly GreenToken? colonColonToken;
    private readonly GreenToken? starToken;
    private readonly GreenToken? openBraceToken;
    private readonly GreenSeparatedList? items;
    private readonly GreenToken? closeBraceToken;
    private readonly GreenToken? asKeyword;
    private readonly GreenToken? alias;

    internal UseDirectiveSyntax(
        GreenToken keyword,
        NameExpressionSyntax path,
        GreenToken? colonColonToken,
        GreenToken? starToken,
        GreenToken? openBraceToken,
        GreenSeparatedList? items,
        GreenToken? closeBraceToken,
        GreenToken? asKeyword,
        GreenToken? alias)
        : base(SyntaxKind.UseDirective, keyword.FullWidth + path.FullWidth + (colonColonToken?.FullWidth ?? 0) + (starToken?.FullWidth ?? 0) + (openBraceToken?.FullWidth ?? 0) + (items?.FullWidth ?? 0) + (closeBraceToken?.FullWidth ?? 0) + (asKeyword?.FullWidth ?? 0) + (alias?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.path = path;
        this.colonColonToken = colonColonToken;
        this.starToken = starToken;
        this.openBraceToken = openBraceToken;
        this.items = items;
        this.closeBraceToken = closeBraceToken;
        this.asKeyword = asKeyword;
        this.alias = alias;
    }

    /// <inheritdoc/>
    public override int SlotCount => 9;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.path,
        2 => this.colonColonToken,
        3 => this.starToken,
        4 => this.openBraceToken,
        5 => this.items,
        6 => this.closeBraceToken,
        7 => this.asKeyword,
        8 => this.alias,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.UseDirectiveSyntax(tree, parent, this, position);
}
