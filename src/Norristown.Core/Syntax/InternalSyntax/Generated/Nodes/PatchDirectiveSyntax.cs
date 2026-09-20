// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.PatchDirectiveSyntax"/>.
/// <c>.patch @op</c>: the one instruction the store above writes into.
/// </summary>
internal sealed class PatchDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenNode target;

    internal PatchDirectiveSyntax(
        GreenToken keyword,
        GreenNode target)
        : base(SyntaxKind.PatchDirective, keyword.FullWidth + target.FullWidth)
    {
        this.keyword = keyword;
        this.target = target;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.target,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.PatchDirectiveSyntax(tree, parent, this, position);
}
