// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ErrorDirectiveSyntax"/>.
/// <c>.error "message"</c> or <c>.warning "message"</c>.
/// </summary>
internal sealed class ErrorDirectiveSyntax : StatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken message;

    internal ErrorDirectiveSyntax(
        GreenToken keyword,
        GreenToken message)
        : base(SyntaxKind.ErrorDirective, keyword.FullWidth + message.FullWidth)
    {
        this.keyword = keyword;
        this.message = message;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.message,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ErrorDirectiveSyntax(tree, parent, this, position);
}
