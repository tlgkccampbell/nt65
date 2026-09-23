using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What an operand written in a macro body becomes once its <c>operand</c> parameter is
/// replaced by the argument: the operand the call gave, and the byte of it the body asked for.
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
    /// one written unbraced, which is an expression and so counts as a plain address.
    /// </summary>
    public bool IsAddress => Operand is AbsoluteOperandSyntax || !IsOperandForm;

    /// <summary>The address-size prefix the argument wrote, if any.</summary>
    public AddressPrefixSyntax? Prefix => (Operand as AbsoluteOperandSyntax)?.Prefix;

    /// <summary>Whether the argument was written braced, as one of the operand forms.</summary>
    private bool IsOperandForm => Operand is OperandSyntax;

    /// <summary>The expression the argument addresses, which is what an address size comes from.</summary>
    public SyntaxNode? Expression => Operands.ExpressionOf(Operand);

    /// <summary>
    /// Whether the mode the argument gave has a next byte at all. An immediate, the
    /// accumulator, an indirect operand and a stack-relative one have no second byte to
    /// name, so <c>dest+1</c> means nothing for them.
    /// </summary>
    public bool HasNextByte => IsAddress && !IndexedByStack;

    /// <summary>The mode as <c>.mode(p)</c> spells it.</summary>
    public string Mode => Operands.ModeOf(Operand);

    /// <summary>The index the argument wrote, such as <c>,x</c>, which follows the expression.</summary>
    public SyntaxToken? Index => (Operand as AbsoluteOperandSyntax)?.IndexRegister;

    /// <summary>
    /// Whether the argument's own index is <c>,s</c>. Only an operand form has an index: an
    /// argument written without braces is an expression, and a name in it that reads as a
    /// register — <c>s</c> itself among them — is still only a name.
    /// </summary>
    private bool IndexedByStack => Operand switch
    {
        AbsoluteOperandSyntax absolute => IsStack(absolute.IndexRegister),
        IndirectOperandSyntax indirect => IsStack(indirect.IndexRegister),
        IndexedIndirectOperandSyntax indexed => IsStack(indexed.InnerRegister) || IsStack(indexed.OuterRegister),
        LongIndirectOperandSyntax far => IsStack(far.IndexRegister),
        _ => false,
    };

    private static bool IsStack(SyntaxToken? register) =>
        register is { } written && written.Text.Equals("s", StringComparison.OrdinalIgnoreCase);
}
