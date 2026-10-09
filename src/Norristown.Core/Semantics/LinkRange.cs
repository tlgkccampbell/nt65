using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Works out the range of values an expression that only the linker can finish may take. An
/// address is somewhere in the space its address size reaches, and each operator narrows or
/// widens that range, so <c>'0' + (main / 10) .mod 10</c> is always a digit wherever
/// <c>main</c> lands. A call to a <c>.func</c> takes the range of its body, with each parameter
/// taking the range of what it is given. ca65 refuses an expression that names an absolute or
/// far address in a one-byte slot, whatever its value, unless a byte operator takes one byte of
/// the address. The output therefore narrows such an expression whose range fits a byte, and
/// the analysis refuses one whose range it cannot show fits.
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
    /// Returns whether ca65 would refuse <paramref name="expression"/> in a one-byte slot because
    /// of its address size. That is the case when nt65 does not know its value and it names an
    /// absolute or far address outside every byte operator, directly or through the body of a
    /// <c>.func</c> it calls. <paramref name="on"/> is the expansion the expression is in.
    /// </summary>
    public static bool NamesWideAddress(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        model.ValueOf(expression, on).AsNumber() is null && NamesWide(model, expression, on, []);

    /// <summary>
    /// Returns whether the output writes <paramref name="expression"/>, the value of a one-byte
    /// slot, inside <c>.lobyte()</c>. That is the case when ca65 would refuse it for its address
    /// size, as <see cref="NamesWideAddress"/> decides, and its range always fits a byte, signed
    /// or unsigned, so that keeping the low byte loses nothing.
    /// </summary>
    public static bool NarrowsToByte(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        NamesWideAddress(model, expression, on) && FitsByte(Of(model, expression, on));

    /// <summary>Returns whether a range, where there is one, lies within -128 to 255.</summary>
    public static bool FitsByte((long Low, long High)? range) => range is { Low: >= -0x80, High: <= 0xff };

    /// <summary>
    /// Returns whether part of an expression names an absolute or far address outside every byte
    /// operator. A part whose value nt65 knows is written as that value, so it names nothing. A
    /// call to a <c>.func</c> names what its arguments and its body name, and
    /// <paramref name="visiting"/> holds the functions already entered, so a recursive body ends.
    /// </summary>
    private static bool NamesWide(SemanticModel model, SyntaxNode node, Expansion? on, HashSet<Symbol> visiting)
    {
        if (node is ExpressionSyntax && model.ValueOf(node, on).AsNumber() is not null)
            return false;
        switch (node)
        {
            case UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret }:
            case CallExpressionSyntax { BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte or BuiltinKind.Bankof }:
                return false;

            case NameExpressionSyntax bound when model.BoundItemOf(bound, on) is { } item:
                return NamesWide(model, item, on, visiting);

            case NameExpressionSyntax name:
                return model.SymbolOf(name, on) is { IsAddress: true }
                    && model.AddressSizeOf(name, null, on) is AddressSize.Absolute or AddressSize.Far;

            case CallExpressionSyntax { Callee: { } callee } call
                when model.SymbolOf(callee, on) is { Kind: SymbolKind.Func, Items: [var body, ..] } function:
                if (!visiting.Add(function))
                    return false;
                try
                {
                    return call.Arguments.ChildNodes.Append(body).Any(part => NamesWide(model, part, on, visiting));
                }
                finally
                {
                    visiting.Remove(function);
                }

            // The output writes `.loword`, `.hiword` and `.endof` around the names they are given,
            // and a `.select` or a `.switch` as the value it chooses. Every other built-in is
            // written as its value or stands for no address.
            case CallExpressionSyntax { Callee: null, BuiltinKind: BuiltinKind.Loword or BuiltinKind.Hiword or BuiltinKind.Endof } builtin:
                return builtin.Arguments.ChildNodes.Any(argument => NamesWide(model, argument, on, visiting));
            case CallExpressionSyntax { Callee: null } chooser:
                return model.ChosenBy(chooser, on) is { } chosen && NamesWide(model, chosen, on, visiting);

            case ParenthesizedExpressionSyntax or UnaryExpressionSyntax or BinaryExpressionSyntax or ArgumentListSyntax:
                return node.ChildNodes.Any(child => NamesWide(model, child, on, visiting));

            default:
                return false;
        }
    }

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
            return Within(number, number);

        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Range(model, parenthesized.Expression, on, parameters, visiting);

            // A name a macro call or a repetition binds to an expression takes that expression's range.
            case NameExpressionSyntax bound when model.BoundItemOf(bound, on) is { } item:
                return Range(model, item, on, parameters, visiting);

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

            // A byte or a word of a value is bounded by its width, whatever the value is.
            case CallExpressionSyntax { BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte or BuiltinKind.Bankof }:
                return (0, 0xff);
            case CallExpressionSyntax { BuiltinKind: BuiltinKind.Loword or BuiltinKind.Hiword }:
                return (0, 0xffff);

            // Membership is 1 or 0, as a comparison is.
            case BinaryExpressionSyntax membership when Evaluator.IsIn(membership.OperatorToken):
                return (0, 1);

            case BinaryExpressionSyntax binary:
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
    /// on an operand that is not negative. A shift is bounded only by fewer than 32 places, since
    /// C leaves a shift as wide as ld65's 32-bit <c>long</c> undefined.
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
            case SyntaxKind.GreaterGreater when positive && constant is >= 0 and < 32 and var count:
                return (a.Low >> (int)count, a.High >> (int)count);
            case SyntaxKind.LessLess when positive && constant is >= 0 and < 32 and var count:
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

    /// <summary>
    /// Returns a range when both of its ends fit in 32 bits, signed, or null when either does not.
    /// ld65 works an expression out in C's <c>long</c>, which is 32 bits on Windows and 64 on
    /// Linux. A value past 32 bits would therefore differ by platform, so nt65 bounds only a
    /// range whose every step stays within 32 bits.
    /// </summary>
    private static (long Low, long High)? Within(Int128 low, Int128 high) =>
        low >= int.MinValue && high <= int.MaxValue ? ((long)low, (long)high) : null;
}
