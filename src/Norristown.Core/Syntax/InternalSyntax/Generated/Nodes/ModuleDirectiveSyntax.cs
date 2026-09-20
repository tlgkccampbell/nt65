// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ModuleDirectiveSyntax"/>.
/// <c>.module name</c> or <c>.module outer::inner</c>.
/// </summary>
internal sealed class ModuleDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenNode name;

    internal ModuleDirectiveSyntax(
        GreenToken keyword,
        GreenNode name)
        : base(SyntaxKind.ModuleDirective, keyword.FullWidth + name.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ModuleDirectiveSyntax(tree, parent, this, position);
}
