using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents what an operand in a macro body becomes once its <c>operand</c> parameter is
/// replaced by the argument. It holds the operand the call gave and the byte of it that the body
/// asked for.
/// </summary>
/// <param name="Parameter">The parameter the body named.</param>
/// <param name="Operand">The operand the call gave, with the braces removed.</param>
/// <param name="Offset">The <c>+ n</c> the body applied, or the byte a <c>.byteof</c> asked for.</param>
/// <param name="ByteOf">
/// Whether the body used <c>.byteof</c>, which shifts an immediate rather than adding to it.
/// </param>
/// <param name="At">The node in the body, which a diagnostic about the substitution points at.</param>
public sealed record OperandSubstitution(
    MacroParameter Parameter,
    SyntaxNode Operand,
    long Offset,
    bool ByteOf,
    SyntaxNode At)
{
    /// <summary>
    /// Gets a value indicating whether the argument is a plain address. That is the case for a
    /// braced address operand, and for an unbraced argument, which is an expression and so counts
    /// as a plain address.
    /// </summary>
    public bool IsAddress => Operand is AbsoluteOperandSyntax || !IsOperandForm;

    /// <summary>Gets the argument's address-size prefix, if any.</summary>
    public AddressPrefixSyntax? Prefix => (Operand as AbsoluteOperandSyntax)?.Prefix;

    /// <summary>
    /// Gets a value indicating whether the argument was braced, as one of the operand forms.
    /// </summary>
    private bool IsOperandForm => Operand is OperandSyntax;

    /// <summary>
    /// Gets the expression the argument addresses, from which the address size is determined.
    /// </summary>
    public SyntaxNode? Expression => Operands.ExpressionOf(Operand);

    /// <summary>
    /// Gets a value indicating whether the argument's addressing mode has a next byte at all. An
    /// immediate, the accumulator, an indirect operand and a stack-relative operand have no second
    /// byte to name, so <c>dest+1</c> means nothing for them.
    /// </summary>
    public bool HasNextByte => IsAddress && !IndexedByStack;

    /// <summary>Gets the addressing mode in the form <c>.mode(p)</c> returns.</summary>
    public string Mode => Operands.ModeOf(Operand);

    /// <summary>Gets the argument's index register, such as <c>,x</c>, which follows the expression.</summary>
    public SyntaxToken? Index => (Operand as AbsoluteOperandSyntax)?.IndexRegister;

    /// <summary>
    /// Gets a value indicating whether the argument's own index is <c>,s</c>. Only an operand form
    /// has an index. An argument without braces is an expression, and a name in it that looks like
    /// a register, including <c>s</c> itself, is still only a name.
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
