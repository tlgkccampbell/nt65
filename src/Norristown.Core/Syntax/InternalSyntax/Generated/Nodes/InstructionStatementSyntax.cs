// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.InstructionStatementSyntax"/>.
/// A mnemonic and its operand, if it takes one.
/// </summary>
internal sealed class InstructionStatementSyntax : StatementSyntax
{
    private readonly GreenToken mnemonic;
    private readonly GreenNode? operand;

    internal InstructionStatementSyntax(
        GreenToken mnemonic,
        GreenNode? operand)
        : base(SyntaxKind.InstructionStatement, mnemonic.FullWidth + (operand?.FullWidth ?? 0))
    {
        this.mnemonic = mnemonic;
        this.operand = operand;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.mnemonic,
        1 => this.operand,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.InstructionStatementSyntax(tree, parent, this, position);
}
