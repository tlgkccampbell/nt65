// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.DataDirectiveSyntax"/>.
/// A data directive: <c>.byte 1, 2</c>, <c>.word[4] { … }</c>, <c>.type T { x = 1 }</c>, <c>.incbin "f"</c>.
/// </summary>
internal sealed class DataDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken directive;
    private readonly GreenNode? type;
    private readonly GreenNode? count;
    private readonly DataTailSyntax? tail;

    internal DataDirectiveSyntax(
        GreenToken directive,
        GreenNode? type,
        GreenNode? count,
        DataTailSyntax? tail)
        : base(SyntaxKind.DataDirective, directive.FullWidth + (type?.FullWidth ?? 0) + (count?.FullWidth ?? 0) + (tail?.FullWidth ?? 0))
    {
        this.directive = directive;
        this.type = type;
        this.count = count;
        this.tail = tail;
    }

    /// <inheritdoc/>
    public override int SlotCount => 4;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.directive,
        1 => this.type,
        2 => this.count,
        3 => this.tail,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.DataDirectiveSyntax(tree, parent, this, position);
}
