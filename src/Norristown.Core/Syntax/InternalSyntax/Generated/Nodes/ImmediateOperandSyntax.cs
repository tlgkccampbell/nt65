// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ImmediateOperandSyntax"/>.
/// <c>#expr</c>, or the <c>#src, #dst</c> of a block move.
/// </summary>
internal sealed class ImmediateOperandSyntax : OperandSyntax
{
    private readonly GreenToken hashToken;
    private readonly GreenNode value;
    private readonly GreenToken? commaToken;
    private readonly GreenToken? secondHashToken;
    private readonly GreenNode? secondValue;

    internal ImmediateOperandSyntax(
        GreenToken hashToken,
        GreenNode value,
        GreenToken? commaToken,
        GreenToken? secondHashToken,
        GreenNode? secondValue)
        : base(SyntaxKind.ImmediateOperand, hashToken.FullWidth + value.FullWidth + (commaToken?.FullWidth ?? 0) + (secondHashToken?.FullWidth ?? 0) + (secondValue?.FullWidth ?? 0))
    {
        this.hashToken = hashToken;
        this.value = value;
        this.commaToken = commaToken;
        this.secondHashToken = secondHashToken;
        this.secondValue = secondValue;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.hashToken,
        1 => this.value,
        2 => this.commaToken,
        3 => this.secondHashToken,
        4 => this.secondValue,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ImmediateOperandSyntax(tree, parent, this, position);
}
