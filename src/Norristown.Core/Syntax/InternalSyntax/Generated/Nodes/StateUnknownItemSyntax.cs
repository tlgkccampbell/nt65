// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateUnknownItemSyntax"/>.
/// <c>?</c> on its own: every tracked part of the processor state is unknown.
/// </summary>
internal sealed class StateUnknownItemSyntax : StateItemSyntax
{
    private readonly GreenToken questionToken;

    internal StateUnknownItemSyntax(
        GreenToken questionToken)
        : base(SyntaxKind.StateUnknownItem, questionToken.FullWidth)
    {
        this.questionToken = questionToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.questionToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateUnknownItemSyntax(tree, parent, this, position);
}
