// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.AccumulatorOperandSyntax"/>.
/// The <c>a</c> of <c>asl a</c>.
/// </summary>
internal sealed class AccumulatorOperandSyntax : OperandSyntax
{
    private readonly GreenToken register;

    internal AccumulatorOperandSyntax(
        GreenToken register)
        : base(SyntaxKind.AccumulatorOperand, register.FullWidth)
    {
        this.register = register;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.register,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.AccumulatorOperandSyntax(tree, parent, this, position);
}
