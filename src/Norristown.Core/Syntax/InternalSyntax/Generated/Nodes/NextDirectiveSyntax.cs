// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.NextDirectiveSyntax"/>.
/// <c>.next @a, gfx::init</c>, or <c>.next ?</c>.
/// </summary>
internal sealed class NextDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken? questionToken;
    private readonly GreenSeparatedList? targets;

    internal NextDirectiveSyntax(
        GreenToken keyword,
        GreenToken? questionToken,
        GreenSeparatedList? targets)
        : base(SyntaxKind.NextDirective, keyword.FullWidth + (questionToken?.FullWidth ?? 0) + (targets?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.questionToken = questionToken;
        this.targets = targets;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.questionToken,
        2 => this.targets,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.NextDirectiveSyntax(tree, parent, this, position);
}
