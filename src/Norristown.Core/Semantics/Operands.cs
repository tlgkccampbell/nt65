using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Reads an operand written in a macro body for the <c>operand</c> parameter it names. An
/// operand parameter stands as a whole operand, so the addressing mode, the address size
/// and the text all come from the argument rather than from the body (§11.2), and layout
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
        if (on is null || operand is not { Kind: SyntaxKind.AbsoluteOperand })
            return null;

        // Only a bare expression can name one: a prefix or an index of its own would be
        // written around a whole operand, which is not something the language allows.
        if (operand.ChildNodes.Length != 1)
            return null;
        return Read(model, operand.ChildNodes[0], on);
    }

    /// <summary>The same, for the expression inside an operand.</summary>
    private static OperandSubstitution? Read(SemanticModel model, SyntaxNode expression, Expansion? on)
    {
        switch (expression.Kind)
        {
            case SyntaxKind.NameExpression:
                return Bound(model, expression, on) is { } plain
                    ? new OperandSubstitution(plain.Parameter, plain.Operand, 0, false, expression)
                    : null;

            // `dest + 1` and `dest - 1` apply to the argument's expression, so that `dest+1`
            // with `dest` bound to `buf,x` is `buf+1,x`.
            case SyntaxKind.BinaryExpression when expression.ChildNodes.Length == 2
                && expression.ChildTokens.Length > 0
                && expression.ChildTokens[0].Kind is SyntaxKind.Plus or SyntaxKind.Minus:
                if (Bound(model, expression.ChildNodes[0], on) is not { } shifted)
                    return null;
                if (model.ValueOf(expression.ChildNodes[1], on).AsNumber() is not { } by)
                    return null;
                var sign = expression.ChildTokens[0].Kind == SyntaxKind.Minus ? -1 : 1;
                return new OperandSubstitution(
                    shifted.Parameter, shifted.Operand, sign * by, false, expression);

            // `.byteof(p, n)` stands where the operand may, and is byte n of its value.
            case SyntaxKind.CallExpression when IsByteOf(expression):
                var given = expression.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ArgumentList);
                var arguments = given?.ChildNodes ?? [];
                if (arguments.Length < 1 || Bound(model, arguments[0], on) is not { } whole)
                    return null;
                var byteAt = arguments.Length > 1 ? model.ValueOf(arguments[1], on).AsNumber() ?? 0 : 0;
                return new OperandSubstitution(whole.Parameter, whole.Operand, byteAt, true, expression);

            default:
                return null;
        }
    }

    /// <summary>
    /// The mode an operand is in, as <c>.mode(p)</c> spells it (§11.2). An argument written
    /// without braces is an expression, and a plain address operand by being one.
    /// </summary>
    public static string ModeOf(SyntaxNode operand) => operand.Kind switch
    {
        SyntaxKind.ImmediateOperand => "imm",
        SyntaxKind.AccumulatorOperand => "acc",
        SyntaxKind.IndirectOperand => IndexedBy(operand, "y") ? "indy" : "ind",
        SyntaxKind.IndexedIndirectOperand => IndexedBy(operand, "s") ? "sry" : "indx",
        SyntaxKind.LongIndirectOperand => IndexedBy(operand, "y") ? "longy" : "long",
        SyntaxKind.AbsoluteOperand when IndexedBy(operand, "s") => "sr",
        SyntaxKind.AbsoluteOperand when IndexedBy(operand, "x") => "absx",
        SyntaxKind.AbsoluteOperand when IndexedBy(operand, "y") => "absy",
        _ => "abs",
    };

    private static bool IndexedBy(SyntaxNode operand, string register)
    {
        foreach (var token in operand.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Register && token.Text.Equals(register, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Whether a call is <c>.byteof</c>.</summary>
    public static bool IsByteOf(SyntaxNode call) =>
        call.Kind == SyntaxKind.CallExpression && call.ChildTokens.Length > 0
        && call.ChildTokens[0].Text.Equals(".byteof", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The operand parameter a name stands for at this expansion, and the operand it was
    /// given. A parameter with no argument, or one of any other kind, is not one of these.
    /// </summary>
    private static (MacroParameter Parameter, SyntaxNode Operand)? Bound(
        SemanticModel model, SyntaxNode name, Expansion? on)
    {
        if (name.Kind != SyntaxKind.NameExpression
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
