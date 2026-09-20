// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BlankLineSyntax"/>.
/// A line holding nothing, or nothing but a comment.
/// </summary>
internal sealed class BlankLineSyntax : StatementSyntax
{
    internal BlankLineSyntax()
        : base(SyntaxKind.BlankLine, 0)
    {
    }

    /// <inheritdoc/>
    public override int SlotCount => 0;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BlankLineSyntax(tree, parent, this, position);
}
