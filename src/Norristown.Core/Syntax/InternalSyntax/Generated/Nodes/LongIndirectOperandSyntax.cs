// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.LongIndirectOperandSyntax"/>.
/// <c>[expr]</c> or <c>[expr],y</c>.
/// </summary>
internal sealed class LongIndirectOperandSyntax : OperandSyntax
{
    private readonly GreenToken openBracketToken;
    private readonly GreenNode address;
    private readonly GreenToken closeBracketToken;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? indexRegister;

    internal LongIndirectOperandSyntax(
        GreenToken openBracketToken,
        GreenNode address,
        GreenToken closeBracketToken,
        GreenToken? commaToken,
        GreenToken? indexRegister)
        : base(SyntaxKind.LongIndirectOperand, openBracketToken.FullWidth + address.FullWidth + closeBracketToken.FullWidth + (commaToken?.FullWidth ?? 0) + (indexRegister?.FullWidth ?? 0))
    {
        this.openBracketToken = openBracketToken;
        this.address = address;
        this.closeBracketToken = closeBracketToken;
        this.commaToken = commaToken;
        this.indexRegister = indexRegister;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBracketToken,
        1 => this.address,
        2 => this.closeBracketToken,
        3 => this.commaToken,
        4 => this.indexRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.LongIndirectOperandSyntax(tree, parent, this, position);
}
