// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BlockContinuationSyntax"/>.
/// <c>} name {</c>: the line that closes one block argument of a macro call and opens the next.
/// </summary>
internal sealed class BlockContinuationSyntax : StatementSyntax
{
    private readonly GreenToken closeBraceToken;
    private readonly GreenToken name;
    private readonly GreenToken openBraceToken;

    internal BlockContinuationSyntax(
        GreenToken closeBraceToken,
        GreenToken name,
        GreenToken openBraceToken)
        : base(SyntaxKind.BlockContinuation, closeBraceToken.FullWidth + name.FullWidth + openBraceToken.FullWidth)
    {
        this.closeBraceToken = closeBraceToken;
        this.name = name;
        this.openBraceToken = openBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.closeBraceToken,
        1 => this.name,
        2 => this.openBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BlockContinuationSyntax(tree, parent, this, position);
}
