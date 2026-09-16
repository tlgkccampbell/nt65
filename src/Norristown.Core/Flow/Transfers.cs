using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What each statement does to the path running through it, and which expression names
/// where it goes. Every quirk has a syntactic fingerprint, so this reads off the mnemonic
/// and the addressing mode alone.
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
        if (statement.Kind != SyntaxKind.InstructionStatement || statement.ChildTokens.Length == 0)
            return Transfer.Through;

        var mnemonic = statement.ChildTokens[0].Text;
        if (Is(mnemonic, "rts") || Is(mnemonic, "rti") || Is(mnemonic, "rtl"))
            return Transfer.Return;
        if (Is(mnemonic, "bra"))
            return Transfer.Jump;
        if (SyntaxFacts.LongBranches.Contains(mnemonic))
            return Transfer.Branch;

        // An indirect or computed target is one the operand does not name, whichever
        // instruction reaches it.
        if (Is(mnemonic, "jsr") || Is(mnemonic, "jsl"))
            return mode == AddressingMode.Absolute ? Transfer.Call : Transfer.Elsewhere;
        if (Is(mnemonic, "jmp") || Is(mnemonic, "jml") || Is(mnemonic, "brl"))
            return mode == AddressingMode.Absolute ? Transfer.Jump : Transfer.Elsewhere;

        return mode is AddressingMode.Relative or AddressingMode.DirectRelative
            ? Transfer.Branch
            : Transfer.Through;
    }

    /// <summary>
    /// The expression a transfer names as its target, or null when it names none.
    /// <c>bbr0 flags, @skip</c> branches to the second of its two expressions; everything
    /// else names its only one.
    /// </summary>
    public static SyntaxNode? TargetOf(SyntaxNode statement, AddressingMode? mode)
    {
        if (statement.ChildNodes.FirstOrDefault() is not { } operand)
            return null;
        var written = operand.ChildNodes.Where(child => child.Kind != SyntaxKind.AddressPrefix).ToList();
        if (mode == AddressingMode.DirectRelative)
            return written.Count > 1 ? written[1] : null;
        return written.Count > 0 ? written[0] : operand;
    }

    private static bool Is(string mnemonic, string name) =>
        mnemonic.Equals(name, StringComparison.OrdinalIgnoreCase);
}
