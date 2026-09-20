// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.AbsoluteOperandSyntax"/>.
/// <c>expr</c>, <c>expr,x</c>, <c>z:expr</c>, or the <c>expr, expr</c> of a bit branch.
/// </summary>
internal sealed class AbsoluteOperandSyntax : OperandSyntax
{
    private readonly GreenNode? prefix;
    private readonly GreenNode address;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? indexRegister;
    private readonly GreenNode? second;

    internal AbsoluteOperandSyntax(
        GreenNode? prefix,
        GreenNode address,
        GreenToken? commaToken,
        GreenToken? indexRegister,
        GreenNode? second)
        : base(SyntaxKind.AbsoluteOperand, (prefix?.FullWidth ?? 0) + address.FullWidth + (commaToken?.FullWidth ?? 0) + (indexRegister?.FullWidth ?? 0) + (second?.FullWidth ?? 0))
    {
        this.prefix = prefix;
        this.address = address;
        this.commaToken = commaToken;
        this.indexRegister = indexRegister;
        this.second = second;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.prefix,
        1 => this.address,
        2 => this.commaToken,
        3 => this.indexRegister,
        4 => this.second,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.AbsoluteOperandSyntax(tree, parent, this, position);
}
