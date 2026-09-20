// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ParameterKindSyntax"/>.
/// What a macro parameter takes: a fixed word, a <c>one(...)</c> of listed words, or a <c>list(...)</c> of one of those.
/// </summary>
internal sealed class ParameterKindSyntax : GreenNode
{
    private readonly GreenToken keyword;
    private readonly GreenToken? openParenToken;
    private readonly GreenSeparatedList? words;
    private readonly GreenNode? element;
    private readonly GreenToken? closeParenToken;

    internal ParameterKindSyntax(
        GreenToken keyword,
        GreenToken? openParenToken,
        GreenSeparatedList? words,
        GreenNode? element,
        GreenToken? closeParenToken)
        : base(SyntaxKind.ParameterKind, keyword.FullWidth + (openParenToken?.FullWidth ?? 0) + (words?.FullWidth ?? 0) + (element?.FullWidth ?? 0) + (closeParenToken?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.openParenToken = openParenToken;
        this.words = words;
        this.element = element;
        this.closeParenToken = closeParenToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.openParenToken,
        2 => this.words,
        3 => this.element,
        4 => this.closeParenToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ParameterKindSyntax(tree, parent, this, position);
}
