using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Works out the range of values an expression that only the linker can finish may take. An
/// address is somewhere in the space its address size reaches, and each operator narrows or
/// widens that range, so <c>'0' + (main / 10) .mod 10</c> is always a digit wherever
/// <c>main</c> lands. A call to a <c>.func</c> takes the range of its body, with each parameter
/// taking the range of what it is given. ca65 refuses an expression that names an absolute
/// address in a one-byte slot, whatever its value, so the output narrows a call whose range
/// fits a byte, and the analysis refuses one whose range it cannot show fits.
/// </summary>
internal static class LinkRange
{
    /// <summary>
    /// Returns the range of values <paramref name="expression"/> may take once linked, or null
    /// where nt65 cannot bound it. <paramref name="on"/> is the expansion the expression is in.
    /// </summary>
    public static (long Low, long High)? Of(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        Range(model, expression, on, null, []);

    /// <summary>
    /// Returns whether <paramref name="expression"/> holds a call to a <c>.func</c> that is given
    /// an address, whose value only the linker knows, and that the output therefore writes as
    /// the function's body.
    /// </summary>
    public static bool HasLinkedCall(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        expression.DescendantNodes().Prepend(expression).OfType<CallExpressionSyntax>().Any(call =>
            call.Callee is { } callee && model.SymbolOf(callee, on) is { Kind: SymbolKind.Func, Items: [var body, ..] } function
            && FunctionArguments.Match(function, call) is { } arguments
            && model.ValueOf(call, on).Kind == ValueKind.Unknown
            && arguments.Cast<SyntaxNode>().Append(body).Any(part => NamesAnAddress(model, part, on)));

    /// <summary>Returns whether an expression names an address anywhere in it.</summary>
    private static bool NamesAnAddress(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        expression.DescendantNodes().Prepend(expression).OfType<NameExpressionSyntax>()
            .Any(name => model.SymbolOf(name, on) is { IsAddress: true });

    /// <summary>
    /// Returns the range of a part of an expression, with each parameter in
    /// <paramref name="parameters"/> taking the value and range it is given, or null where the
    /// range is not bounded.
    /// </summary>
    private static (long Low, long High)? Range(
        SemanticModel model, SyntaxNode node, Expansion? on,
        IReadOnlyDictionary<Symbol, (Value Value, (long Low, long High)? Range)>? parameters, HashSet<Symbol> visiting)
    {
        var value = parameters is null
            ? model.ValueOf(node, on)
            : model.ValueOf(node, parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value), on);
        if (value.AsNumber() is { } number)
            return (number, number);

        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Range(model, parenthesized.Expression, on, parameters, visiting);

            case NameExpressionSyntax name when model.SymbolOf(name, on) is { } symbol:
                if (parameters is not null && parameters.TryGetValue(symbol, out var given))
                    return given.Range;
                if (!symbol.IsAddress)
                    return null;
                return model.AddressSizeOf(name, null, on) switch
                {
                    AddressSize.ZeroPage => (0, 0xff),
                    AddressSize.Absolute => (0, 0xffff),
                    AddressSize.Far => (0, 0xffffff),
                    _ => null,
                };

            case UnaryExpressionSyntax unary:
                var operand = Range(model, unary.Operand, on, parameters, visiting);
                return unary.OperatorToken.Kind switch
                {
                    SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret => (0, 0xff),
                    SyntaxKind.Bang => (0, 1),
                    SyntaxKind.Plus => operand,
                    SyntaxKind.Minus when operand is { } negated => Within(-(Int128)negated.High, -(Int128)negated.Low),
                    _ => null,
                };

            case BinaryExpressionSyntax binary when !Evaluator.IsIn(binary.OperatorToken):
                return Binary(binary.OperatorToken,
                    Range(model, binary.Left, on, parameters, visiting), Range(model, binary.Right, on, parameters, visiting));

            case CallExpressionSyntax { Callee: { } callee } call
                when model.SymbolOf(callee, on) is { Kind: SymbolKind.Func, Items: [var body, ..] } function
                && FunctionArguments.Match(function, call) is { } arguments && visiting.Add(function):
                try
                {
                    var bound = new Dictionary<Symbol, (Value Value, (long Low, long High)? Range)>();
                    for (var i = 0; i < arguments.Count; i++)
                    {
                        var argument = parameters is null
                            ? model.ValueOf(arguments[i], on)
                            : model.ValueOf(arguments[i], parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value), on);
                        bound[function.ParameterSymbols[i]] = (argument, Range(model, arguments[i], on, parameters, visiting));
                    }
                    return Range(model, body, on, bound, visiting);
                }
                finally
                {
                    visiting.Remove(function);
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// Returns the range of a binary operation on operands with the ranges
    /// <paramref name="left"/> and <paramref name="right"/>, or null where it is not bounded.
    /// Division, <c>.mod</c> and the shifts are bounded only by a constant that is not negative,
    /// on an operand that is not negative.
    /// </summary>
    private static (long Low, long High)? Binary(SyntaxToken op, (long Low, long High)? left, (long Low, long High)? right)
    {
        // A comparison or a logical operator gives 0 or 1, whatever its operands are.
        if (op.Kind is SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals
            or SyntaxKind.EqualsEquals or SyntaxKind.BangEquals or SyntaxKind.AmpersandAmpersand
            or SyntaxKind.BarBar or SyntaxKind.CaretCaret)
        {
            return (0, 1);
        }
        if (left is not { } a || right is not { } b)
            return null;
        var constant = b.Low == b.High ? b.Low : (long?)null;
        var positive = a.Low >= 0;
        switch (op.Kind)
        {
            case SyntaxKind.Plus:
                return Within((Int128)a.Low + b.Low, (Int128)a.High + b.High);
            case SyntaxKind.Minus:
                return Within((Int128)a.Low - b.High, (Int128)a.High - b.Low);
            case SyntaxKind.Star:
                Int128[] corners = [(Int128)a.Low * b.Low, (Int128)a.Low * b.High, (Int128)a.High * b.Low, (Int128)a.High * b.High];
                return Within(corners.Min(), corners.Max());
            case SyntaxKind.Slash when positive && constant is > 0 and var divisor:
                return (a.Low / divisor, a.High / divisor);
            case SyntaxKind.Directive when positive && constant is > 0 and var modulus:
                return a.High < modulus ? a : (0, modulus - 1);
            case SyntaxKind.GreaterGreater when positive && constant is >= 0 and < 64 and var count:
                return (a.Low >> (int)count, a.High >> (int)count);
            case SyntaxKind.LessLess when positive && constant is >= 0 and < 64 and var count:
                return Within((Int128)a.Low << (int)count, (Int128)a.High << (int)count);
            case SyntaxKind.Ampersand when positive && b.Low >= 0:
                return (0, Math.Min(a.High, b.High));
            case SyntaxKind.Bar or SyntaxKind.Caret when positive && b.Low >= 0:
                // Neither operand sets a bit above the highest bit of the larger one.
                var highest = Math.Max(a.High, b.High);
                return (0, highest == 0 ? 0 : (long)((1UL << (64 - (int)long.LeadingZeroCount(highest))) - 1));
            default:
                return null;
        }
    }

    /// <summary>Returns a range when both of its ends fit in 64 bits, or null when either does not.</summary>
    private static (long Low, long High)? Within(Int128 low, Int128 high) =>
        low >= long.MinValue && high <= long.MaxValue ? ((long)low, (long)high) : null;
}
