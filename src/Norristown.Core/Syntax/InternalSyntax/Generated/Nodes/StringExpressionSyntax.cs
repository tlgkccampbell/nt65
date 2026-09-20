// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StringExpressionSyntax"/>.
/// A string in double quotes.
/// </summary>
internal sealed class StringExpressionSyntax : LiteralExpressionSyntax
{
    private readonly GreenToken token;

    internal StringExpressionSyntax(GreenToken token)
        : base(SyntaxKind.StringExpression, token.FullWidth)
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
        new Red.StringExpressionSyntax(tree, parent, this, position);
}
