using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Reads an operand in a macro body for the <c>operand</c> parameter it names. An operand
/// parameter is substituted as a whole operand, so the addressing mode, the address size and the
/// text all come from the argument rather than from the body. Layout and emission both use these
/// methods, because they must agree about which operand a line really has.
/// </summary>
public static class Operands
{
    /// <summary>
    /// Returns what <paramref name="operand"/> becomes in the expansion <paramref name="on"/>, or
    /// null when it names no <c>operand</c> parameter and stays as written.
    /// </summary>
    public static OperandSubstitution? Substituted(SemanticModel model, SyntaxNode? operand, Expansion? on)
    {
        if (on is null || operand is not AbsoluteOperandSyntax absolute)
            return null;

        // Only a bare expression can name an operand parameter. A prefix or an index of its own
        // would wrap a whole operand, which the language does not allow.
        if (absolute.Prefix is not null || absolute.Second is not null)
            return null;
        return Read(model, absolute.Address, on);
    }

    /// <summary>
    /// Returns the expression an operand addresses, or its immediate value. For example, this is
    /// <c>buf</c> for <c>buf,x</c>, <c>ptr</c> for <c>(ptr),y</c> and <c>5</c> for <c>#5</c>. An
    /// argument without braces is itself the expression, and the accumulator operand has none.
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

    /// <summary>
    /// Returns the address size given by the operand's prefix, which overrides any other way of
    /// deciding the size. <c>d:</c> makes a direct operand of a constant address, reached through
    /// the direct page.
    /// </summary>
    public static AddressSize? PrefixSize(SyntaxNode operand)
    {
        if (operand is not AbsoluteOperandSyntax { Prefix: { } prefix })
            return null;
        return char.ToLowerInvariant(prefix.Name.Text[0]) switch
        {
            'z' or 'd' => AddressSize.ZeroPage,
            'a' => AddressSize.Absolute,
            'f' => AddressSize.Far,
            _ => null,
        };
    }

    /// <summary>Returns a value indicating whether <paramref name="call"/> calls <c>.exprof</c>.</summary>
    public static bool IsExprOf(CallExpressionSyntax call) => call.BuiltinKind == BuiltinKind.Exprof;

    /// <summary>
    /// Returns the operand's addressing mode in the form <c>.mode(p)</c> returns. An argument
    /// without braces is an expression, so it counts as a plain address operand.
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

    /// <summary>Returns a value indicating whether <paramref name="call"/> calls <c>.byteof</c>.</summary>
    public static bool IsByteOf(CallExpressionSyntax call) => call.BuiltinKind == BuiltinKind.Byteof;

    /// <summary>
    /// Returns what <paramref name="expression"/>, the expression inside an operand, becomes in
    /// the expansion <paramref name="on"/>, or null when it names no <c>operand</c> parameter.
    /// </summary>
    private static OperandSubstitution? Read(SemanticModel model, ExpressionSyntax expression, Expansion? on)
    {
        switch (expression)
        {
            case NameExpressionSyntax:
                return Bound(model, expression, on) is { } plain
                    ? new OperandSubstitution(plain.Parameter, plain.Operand, plain.Offset, false, expression)
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
                    shifted.Parameter, shifted.Operand, shifted.Offset + (sign * by), false, expression);

            // `.byteof(p, n)` may appear where the operand would be, and means byte n of the
            // operand's value. A byte of an immediate is a shift rather than a sum, so an
            // argument that adds to an operand is not followed through to it.
            case CallExpressionSyntax call when IsByteOf(call):
                var arguments = call.Arguments.Arguments;
                if (arguments.Count < 1 || Bound(model, arguments[0], on, shifts: false) is not { } whole)
                    return null;
                var byteAt = arguments.Count > 1 ? model.ValueOf(arguments[1], on).AsNumber() ?? 0 : 0;
                return new OperandSubstitution(whole.Parameter, whole.Operand, byteAt, true, expression);

            default:
                return null;
        }
    }

    private static bool Is(SyntaxToken? register, string name) =>
        register is { } token && token.Text.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the operand parameter <paramref name="name"/> refers to, the operand it was given in
    /// the expansion <paramref name="on"/>, and the offset the operand is taken at. Returns null for
    /// a parameter with no argument or of any other kind.
    /// </summary>
    /// <remarks>
    /// An argument that names an <c>operand</c> parameter of the macro that made the call, alone
    /// or with <c>+ n</c>, is followed to the operand that parameter was given. It is read in the
    /// caller's expansion, as <see cref="SemanticModel.GivenAt"/> gives it. The operand passed
    /// through any number of macros is therefore the operand given once, and is laid out and
    /// written the same way. Where <paramref name="shifts"/> is false, an argument that adds to
    /// the operand it names is not followed.
    /// </remarks>
    private static (MacroParameter Parameter, SyntaxNode Operand, long Offset)? Bound(
        SemanticModel model, SyntaxNode name, Expansion? on, bool shifts = true)
    {
        if (name is not NameExpressionSyntax
            || model.SymbolOf(name) is not { Kind: SymbolKind.MacroParameter, Parameter: { } parameter }
            || parameter.Kind != ParameterKind.Operand)
        {
            return null;
        }
        if (model.GivenAt(parameter.Symbol, on) is not { Argument.Operand: { } operand, Caller: var caller })
            return null;

        // An unbraced argument is an expression, which counts as a plain address operand. A
        // braced argument is already the operand it was written as.
        var passed = operand switch
        {
            AbsoluteOperandSyntax absolute => Substituted(model, absolute, caller),
            ExpressionSyntax expression => Read(model, expression, caller),
            _ => null,
        };
        return passed is { ByteOf: false } && (shifts || passed.Offset == 0)
            ? (parameter, passed.Operand, passed.Offset)
            : (parameter, operand, 0);
    }
}
