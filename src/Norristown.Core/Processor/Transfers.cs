using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>
/// Determines how each statement affects the control flow through it, and which expression
/// names where it goes. Every special case can be recognized from the syntax, so this class
/// decides from the mnemonic and the addressing mode alone.
/// </summary>
public static class Transfers
{
    /// <summary>
    /// Returns how <paramref name="statement"/> affects control flow. <paramref name="mode"/> is
    /// the addressing mode that layout chose for it, which tells <c>jmp t</c> apart from
    /// <c>jmp (t)</c>.
    /// </summary>
    public static Transfer Of(SyntaxNode statement, AddressingMode? mode)
    {
        if (statement is not InstructionStatementSyntax instruction)
            return Transfer.Through;

        // An indirect or computed target is one the operand does not name, whichever
        // instruction reaches it. A stop ends the path as a return does.
        return Instructions.Facts(instruction.MnemonicKind).Control switch
        {
            Control.Returns or Control.Stops => Transfer.Return,
            Control.Branches => Transfer.Branch,
            Control.Jumps => Names(mode) ? Transfer.Jump : Transfer.Elsewhere,
            Control.Calls => Names(mode) ? Transfer.Call : Transfer.Elsewhere,
            _ => Transfer.Through,
        };
    }

    /// <summary>
    /// Returns the expression a transfer names as its target, or null if the statement has no
    /// operand. <c>bbr0 flags, @skip</c> branches to the second of its two expressions. Every
    /// other instruction names its first expression, or the operand itself when the operand
    /// holds no expression.
    /// </summary>
    public static SyntaxNode? TargetOf(SyntaxNode statement, AddressingMode? mode)
    {
        if (statement is not InstructionStatementSyntax { Operand: { } operand })
            return null;
        var expressions = operand.ChildNodes.OfType<ExpressionSyntax>().ToList();
        if (mode == AddressingMode.DirectRelative)
            return expressions.Count > 1 ? expressions[1] : null;
        return expressions.Count > 0 ? expressions[0] : operand;
    }

    /// <summary>
    /// Returns a value indicating whether an operand in <paramref name="mode"/> names the place
    /// it reaches.
    /// </summary>
    private static bool Names(AddressingMode? mode) =>
        mode is AddressingMode.Absolute or AddressingMode.Long or AddressingMode.Relative or AddressingMode.RelativeLong;
}
