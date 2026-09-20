// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ErrorLineSyntax"/>.
/// A line the parser could not read as any statement: its tokens, as they are.
/// </summary>
internal sealed class ErrorLineSyntax : StatementSyntax
{
    private readonly GreenList? tokens;

    internal ErrorLineSyntax(GreenList? tokens)
        : base(SyntaxKind.ErrorLine, (tokens?.FullWidth ?? 0))
    {
        this.tokens = tokens;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.tokens,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ErrorLineSyntax(tree, parent, this, position);
}
