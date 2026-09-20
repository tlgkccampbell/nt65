// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.RecordValuesSyntax"/>.
/// <c>{ member = value, … }</c>: a record's values.
/// </summary>
internal sealed class RecordValuesSyntax : GreenNode
{
    private readonly GreenToken openBraceToken;
    private readonly GreenSeparatedList? members;
    private readonly GreenToken closeBraceToken;

    internal RecordValuesSyntax(
        GreenToken openBraceToken,
        GreenSeparatedList? members,
        GreenToken closeBraceToken)
        : base(SyntaxKind.RecordValues, openBraceToken.FullWidth + (members?.FullWidth ?? 0) + closeBraceToken.FullWidth)
    {
        this.openBraceToken = openBraceToken;
        this.members = members;
        this.closeBraceToken = closeBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBraceToken,
        1 => this.members,
        2 => this.closeBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.RecordValuesSyntax(tree, parent, this, position);
}
