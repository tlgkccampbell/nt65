// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.IndirectOperandSyntax"/>.
/// <c>(expr)</c> or <c>(expr),y</c>.
/// </summary>
internal sealed class IndirectOperandSyntax : OperandSyntax
{
    private readonly GreenToken openParenToken;
    private readonly GreenNode address;
    private readonly GreenToken closeParenToken;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? indexRegister;

    internal IndirectOperandSyntax(
        GreenToken openParenToken,
        GreenNode address,
        GreenToken closeParenToken,
        GreenToken? commaToken,
        GreenToken? indexRegister)
        : base(SyntaxKind.IndirectOperand, openParenToken.FullWidth + address.FullWidth + closeParenToken.FullWidth + (commaToken?.FullWidth ?? 0) + (indexRegister?.FullWidth ?? 0))
    {
        this.openParenToken = openParenToken;
        this.address = address;
        this.closeParenToken = closeParenToken;
        this.commaToken = commaToken;
        this.indexRegister = indexRegister;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openParenToken,
        1 => this.address,
        2 => this.closeParenToken,
        3 => this.commaToken,
        4 => this.indexRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.IndirectOperandSyntax(tree, parent, this, position);
}
