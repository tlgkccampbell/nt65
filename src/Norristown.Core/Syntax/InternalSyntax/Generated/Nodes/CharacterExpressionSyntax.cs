// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.CharacterExpressionSyntax"/>.
/// A character in single quotes.
/// </summary>
internal sealed class CharacterExpressionSyntax : LiteralExpressionSyntax
{
    private readonly GreenToken token;

    internal CharacterExpressionSyntax(
        GreenToken token)
        : base(SyntaxKind.CharacterExpression, token.FullWidth)
    {
        this.token = token;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.token,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.CharacterExpressionSyntax(tree, parent, this, position);
}
