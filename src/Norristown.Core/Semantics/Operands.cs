using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Reads an operand written in a macro body for the <c>operand</c> parameter it names. An
/// operand parameter stands as a whole operand, so the addressing mode, the address size
/// and the text all come from the argument rather than from the body, and layout
/// and emission have to agree about which operand a line really has.
/// </summary>
public static class Operands
{
    /// <summary>
    /// What <paramref name="operand"/> stands for at <paramref name="on"/>, or null when it
    /// names no <c>operand</c> parameter and is simply itself.
    /// </summary>
    public static OperandSubstitution? Substituted(SemanticModel model, SyntaxNode? operand, Expansion? on)
    {
        if (on is null || operand is not AbsoluteOperandSyntax absolute)
            return null;

        // Only a bare expression can name one: a prefix or an index of its own would be
        // written around a whole operand, which is not something the language allows.
        if (absolute.Prefix is not null || absolute.Second is not null)
            return null;
        return Read(model, absolute.Address, on);
    }

    /// <summary>The same, for the expression inside an operand.</summary>
    private static OperandSubstitution? Read(SemanticModel model, ExpressionSyntax expression, Expansion? on)
    {
        switch (expression)
        {
            case NameExpressionSyntax:
                return Bound(model, expression, on) is { } plain
                    ? new OperandSubstitution(plain.Parameter, plain.Operand, 0, false, expression)
                    : null;

            // `dest + 1` and `dest - 1` apply to the argument's expression, so that `dest+1`
            // with `dest` bound to `buf,x` is `buf+1,x`.
            case BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus or SyntaxKind.Minus } binary:
                if (Bound(model, binary.Left, on) is not { } shifted)
                    return null;
                if (model.ValueOf(binary.Right, on).AsNumber() is not { } by)
                    return null;
                var sign = binary.OperatorToken.Kind == SyntaxKind.Minus ? -1 : 1;
                return new OperandSubstitution(
                    shifted.Parameter, shifted.Operand, sign * by, false, expression);

            // `.byteof(p, n)` stands where the operand may, and is byte n of its value.
            case CallExpressionSyntax call when IsByteOf(call):
                var arguments = call.Arguments.Arguments;
                if (arguments.Count < 1 || Bound(model, arguments[0], on) is not { } whole)
                    return null;
                var byteAt = arguments.Count > 1 ? model.ValueOf(arguments[1], on).AsNumber() ?? 0 : 0;
                return new OperandSubstitution(whole.Parameter, whole.Operand, byteAt, true, expression);

            default:
                return null;
        }
    }

    /// <summary>
    /// The expression an operand addresses, or its immediate value: <c>buf</c> of <c>buf,x</c>,
    /// <c>ptr</c> of <c>(ptr),y</c>, <c>5</c> of <c>#5</c>. An argument written without braces is
    /// the expression itself; the accumulator has none.
    /// </summary>
    public static SyntaxNode? ExpressionOf(SyntaxNode operand) => operand switch
    {
        AbsoluteOperandSyntax absolute => absolute.Address,
        ImmediateOperandSyntax immediate => immediate.Value,
        IndirectOperandSyntax indirect => indirect.Address,
        IndexedIndirectOperandSyntax indexed => indexed.Address,
        LongIndirectOperandSyntax far => far.Address,
        AccumulatorOperandSyntax => null,
        _ => operand,
    };

    /// <summary>Whether a call is <c>.exprof</c>.</summary>
    public static bool IsExprOf(CallExpressionSyntax call) =>
        call.Function is { } function
        && function.Text.Equals(".exprof", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mode an operand is in, as <c>.mode(p)</c> spells it. An argument written
    /// without braces is an expression, and a plain address operand by being one.
    /// </summary>
    public static string ModeOf(SyntaxNode operand) => operand switch
    {
        ImmediateOperandSyntax => "imm",
        AccumulatorOperandSyntax => "acc",
        IndirectOperandSyntax indirect => Is(indirect.IndexRegister, "y") ? "indy" : "ind",
        IndexedIndirectOperandSyntax indexed => Is(indexed.InnerRegister, "s") ? "sry" : "indx",
        LongIndirectOperandSyntax far => Is(far.IndexRegister, "y") ? "longy" : "long",
        AbsoluteOperandSyntax absolute when Is(absolute.IndexRegister, "s") => "sr",
        AbsoluteOperandSyntax absolute when Is(absolute.IndexRegister, "x") => "absx",
        AbsoluteOperandSyntax absolute when Is(absolute.IndexRegister, "y") => "absy",
        _ => "abs",
    };

    private static bool Is(SyntaxToken? register, string name) =>
        register is { } written && written.Text.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a call is <c>.byteof</c>.</summary>
    public static bool IsByteOf(CallExpressionSyntax call) =>
        call.Function is { } function
        && function.Text.Equals(".byteof", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The operand parameter a name stands for at this expansion, and the operand it was
    /// given. A parameter with no argument, or one of any other kind, is not one of these.
    /// </summary>
    private static (MacroParameter Parameter, SyntaxNode Operand)? Bound(
        SemanticModel model, SyntaxNode name, Expansion? on)
    {
        if (name is not NameExpressionSyntax
            || model.SymbolOf(name) is not { Kind: SymbolKind.MacroParameter, Parameter: { } parameter }
            || parameter.Kind != ParameterKind.Operand)
        {
            return null;
        }
        if (model.ArgumentFor(parameter.Symbol, on) is not { } argument || argument.Operand is not { } operand)
            return null;

        // An unbraced argument is an expression, and an expression is a plain address
        // operand; a braced one is already the operand it was written as.
        return (parameter, operand);
    }
}
