// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.IndexedIndirectOperandSyntax"/>.
/// <c>(expr,x)</c> or <c>(expr,s),y</c>.
/// </summary>
internal sealed class IndexedIndirectOperandSyntax : OperandSyntax
{
    private readonly GreenToken openParenToken;
    private readonly ExpressionSyntax address;
    private readonly GreenToken commaToken;
    private readonly GreenToken innerRegister;
    private readonly GreenToken closeParenToken;
    private readonly GreenToken? outerCommaToken;
    private readonly GreenToken? outerRegister;

    internal IndexedIndirectOperandSyntax(
        GreenToken openParenToken,
        ExpressionSyntax address,
        GreenToken commaToken,
        GreenToken innerRegister,
        GreenToken closeParenToken,
        GreenToken? outerCommaToken,
        GreenToken? outerRegister)
        : base(SyntaxKind.IndexedIndirectOperand, openParenToken.FullWidth + address.FullWidth + commaToken.FullWidth + innerRegister.FullWidth + closeParenToken.FullWidth + (outerCommaToken?.FullWidth ?? 0) + (outerRegister?.FullWidth ?? 0))
    {
        this.openParenToken = openParenToken;
        this.address = address;
        this.commaToken = commaToken;
        this.innerRegister = innerRegister;
        this.closeParenToken = closeParenToken;
        this.outerCommaToken = outerCommaToken;
        this.outerRegister = outerRegister;
    }

    /// <inheritdoc/>
    public override int SlotCount => 7;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openParenToken,
        1 => this.address,
        2 => this.commaToken,
        3 => this.innerRegister,
        4 => this.closeParenToken,
        5 => this.outerCommaToken,
        6 => this.outerRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.IndexedIndirectOperandSyntax(tree, parent, this, position);
}
