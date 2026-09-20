// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ImportDirectiveSyntax"/>.
/// <c>.import a, b: far, c: proc(a8 -&gt; a8)</c>.
/// </summary>
internal sealed class ImportDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenSeparatedList? items;

    internal ImportDirectiveSyntax(
        GreenToken keyword,
        GreenSeparatedList? items)
        : base(SyntaxKind.ImportDirective, keyword.FullWidth + (items?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.items = items;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.items,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ImportDirectiveSyntax(tree, parent, this, position);
}
