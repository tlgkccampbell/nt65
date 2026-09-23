using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>
/// What each statement does to the path running through it, and which expression names
/// where it goes. Every special case can be recognised from the syntax, so this decides from
/// the mnemonic and the addressing mode alone.
/// </summary>
public static class Transfers
{
    /// <summary>
    /// What <paramref name="statement"/> does to the path. <paramref name="mode"/> is the
    /// addressing mode layout chose for it, which is what tells <c>jmp t</c> from
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
    /// The expression a transfer names as its target, or null when the statement has no
    /// operand. <c>bbr0 flags, @skip</c> branches to the second of its two expressions;
    /// everything else names its first, or the operand itself when it holds no expression.
    /// </summary>
    public static SyntaxNode? TargetOf(SyntaxNode statement, AddressingMode? mode)
    {
        if (statement is not InstructionStatementSyntax { Operand: { } operand })
            return null;
        var written = operand.ChildNodes.OfType<ExpressionSyntax>().ToList();
        if (mode == AddressingMode.DirectRelative)
            return written.Count > 1 ? written[1] : null;
        return written.Count > 0 ? written[0] : operand;
    }

    /// <summary>Whether an operand in <paramref name="mode"/> names the place it reaches.</summary>
    private static bool Names(AddressingMode? mode) =>
        mode is AddressingMode.Absolute or AddressingMode.Long or AddressingMode.Relative or AddressingMode.RelativeLong;
}
