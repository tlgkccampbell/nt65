using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What an operand written in a macro body stands for once its <c>operand</c> parameter
/// does: the operand the call gave, and the byte of it the body asked for.
/// </summary>
/// <param name="Parameter">The parameter the body named.</param>
/// <param name="Operand">The operand the call gave, with the braces off.</param>
/// <param name="Offset">The <c>+ n</c> the body applied, or the byte a <c>.byteof</c> asked for.</param>
/// <param name="ByteOf">Whether it was a <c>.byteof</c>, which shifts an immediate rather than adding to it.</param>
/// <param name="At">What the body wrote, which is what a diagnostic about the pair names.</param>
public sealed record OperandSubstitution(
    MacroParameter Parameter,
    SyntaxNode Operand,
    long Offset,
    bool ByteOf,
    SyntaxNode At)
{
    /// <summary>
    /// Whether the argument is a plain address: one written braced as an address operand, or
    /// one written unbraced, which is an expression and so an address by being one.
    /// </summary>
    public bool IsAddress => Operand.Kind == SyntaxKind.AbsoluteOperand || !IsOperandForm;

    /// <summary>The address-size prefix the argument wrote, if any.</summary>
    public SyntaxNode? Prefix =>
        Operand.Kind == SyntaxKind.AbsoluteOperand
            ? Operand.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.AddressPrefix)
            : null;

    /// <summary>Whether the argument was written braced, as one of the operand forms.</summary>
    private bool IsOperandForm => Operand.Kind is SyntaxKind.AbsoluteOperand or SyntaxKind.ImmediateOperand
        or SyntaxKind.AccumulatorOperand or SyntaxKind.IndirectOperand
        or SyntaxKind.IndexedIndirectOperand or SyntaxKind.LongIndirectOperand;

    /// <summary>The expression the argument addresses, which is what an address size comes from.</summary>
    public SyntaxNode? Expression => Operand.Kind switch
    {
        SyntaxKind.AbsoluteOperand or SyntaxKind.ImmediateOperand or SyntaxKind.IndirectOperand
            or SyntaxKind.IndexedIndirectOperand or SyntaxKind.LongIndirectOperand =>
            Operand.ChildNodes.FirstOrDefault(child => child.Kind != SyntaxKind.AddressPrefix),
        SyntaxKind.AccumulatorOperand => null,
        _ => Operand,
    };

    /// <summary>
    /// Whether the mode the argument gave has a next byte at all. An immediate, the
    /// accumulator, an indirect operand and a stack-relative one have no second byte to
    /// name, so <c>dest+1</c> means nothing for them.
    /// </summary>
    public bool HasNextByte => IsAddress && !IndexedByStack;

    /// <summary>The mode as <c>.mode(p)</c> spells it.</summary>
    public string Mode => Operands.ModeOf(Operand);

    /// <summary>The index the argument wrote, such as <c>,x</c>, which follows the expression.</summary>
    public SyntaxToken? Index
    {
        get
        {
            if (!IsAddress || Operand.Kind != SyntaxKind.AbsoluteOperand)
                return null;
            foreach (var token in Operand.ChildTokens)
            {
                if (token.Kind == SyntaxKind.Register)
                    return token;
            }
            return null;
        }
    }

    private bool IndexedByStack => IndexedBy("s");

    private bool IndexedBy(string register)
    {
        foreach (var token in Operand.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Register && token.Text.Equals(register, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
