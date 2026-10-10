using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Works out the range of values an expression that only the linker can finish may take. A
/// label, like a segment's linker symbol, is inside the memory areas the linked configurations
/// place its segment in. Where they say nothing, an address is somewhere in the space its address
/// size reaches. Each operator narrows or widens that range, so <c>'0' + (main / 10) .mod 10</c>
/// is always a digit wherever <c>main</c> lands. A call to a <c>.func</c> takes the range of its
/// body, with each parameter taking the range of what it is given.
/// </summary>
/// <remarks>
/// ca65 refuses an expression that names an address wider than its slot, whatever its value,
/// unless an operator takes a part of the address that fits. That is an absolute or far address
/// in a one-byte slot, or a far one in a two-byte slot. ld65 then refuses a value that does not
/// fit its slot, as an address placed past the slot's reach may not. The output therefore
/// narrows such an expression whose range fits the slot, and the analysis refuses one whose range
/// it cannot show fits.
/// </remarks>
internal static class LinkRange
{
    /// <summary>
    /// Returns the range of values <paramref name="expression"/> may take once linked, or null
    /// where nt65 cannot bound it. <paramref name="on"/> is the expansion the expression is in.
    /// </summary>
    public static (long Low, long High)? Of(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        Range(model, expression, on, null, []);

    /// <summary>
    /// Returns whether ca65 or ld65 may refuse <paramref name="expression"/> in a slot of
    /// <paramref name="bytes"/> bytes because of an address it names. That is the case when nt65
    /// does not know its value and it names, directly or through the body of a <c>.func</c> it
    /// calls, an address that is wider than the slot or placed past its reach. An address inside
    /// an operator that takes a part of it that fits the slot does not count.
    /// </summary>
    /// <param name="model">The model the expression is read in.</param>
    /// <param name="expression">The value of the slot.</param>
    /// <param name="on">The expansion the expression is in.</param>
    /// <param name="bytes">The width of the slot, which is one or two bytes.</param>
    /// <param name="placed">
    /// A value indicating whether an address placed past the slot's reach counts. It is false for
    /// an <c>.addr</c>, which holds the address within its bank, as ca65 keeps it on the 65816.
    /// </param>
    public static bool NamesWideAddress(
        SemanticModel model, SyntaxNode expression, Expansion? on, int bytes, bool placed = true) =>
        model.ValueOf(expression, on).AsNumber() is null && NamesWide(model, expression, on, bytes, placed, []);

    /// <summary>
    /// Returns whether the output writes <paramref name="expression"/>, the value of a slot
    /// <paramref name="bytes"/> bytes wide, inside <c>.lobyte()</c> or <c>.loword()</c>. That is
    /// the case when it names an address that ca65 or ld65 may refuse, as
    /// <see cref="NamesWideAddress"/> decides, and its range always fits the slot, signed or
    /// unsigned, so that keeping the low part loses nothing.
    /// </summary>
    public static bool Narrows(SemanticModel model, SyntaxNode expression, Expansion? on, int bytes) =>
        bytes is 1 or 2 && NamesWideAddress(model, expression, on, bytes) && Fits(Of(model, expression, on), bytes);

    /// <summary>
    /// Returns whether a range, where there is one, fits a slot of <paramref name="bytes"/>
    /// bytes, signed or unsigned. One byte takes -128 to 255, and two bytes take -32768 to 65535.
    /// </summary>
    public static bool Fits((long Low, long High)? range, int bytes) =>
        range is { } bound && bound.Low >= -(1L << ((8 * bytes) - 1)) && bound.High < 1L << (8 * bytes);

    /// <summary>
    /// Returns whether part of an expression names an address wider than a slot of
    /// <paramref name="bytes"/> bytes, or one placed past its reach where
    /// <paramref name="placed"/> says that counts, outside every operator that takes a part that
    /// fits. A part whose value nt65 knows is written as that value, so it names nothing. A call
    /// to a <c>.func</c> names what its arguments and its body name, and
    /// <paramref name="visiting"/> holds the functions already entered, so a recursive body ends.
    /// </summary>
    private static bool NamesWide(
        SemanticModel model, SyntaxNode node, Expansion? on, int bytes, bool placed, HashSet<Symbol> visiting)
    {
        if (node is ExpressionSyntax && model.ValueOf(node, on).AsNumber() is not null)
            return false;
        switch (node)
        {
            case UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret }:
            case CallExpressionSyntax { BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte or BuiltinKind.Bankof }:
                return false;

            case NameExpressionSyntax bound when model.BoundItemOf(bound, on) is { } item:
                return NamesWide(model, item, on, bytes, placed, visiting);

            case NameExpressionSyntax name:
                return model.SymbolOf(name, on) is { IsAddress: true } symbol
                    && model.AddressSizeOf(name, null, on) is { } size
                    && Wider(size, AddressRange(model, symbol, size), bytes, placed);

            // A segment function is a symbol ld65 defines, as wide as the segment's placement.
            case CallExpressionSyntax segmental when SegmentFunctions.Of(segmental, model) is { } about:
                return Wider(SegmentFunctions.SizeOf(about.Function, about.Segment),
                    SegmentFunctions.RangeOf(about.Function, about.Segment), bytes, placed);

            // An argument counts only where the body uses its parameter outside every byte
            // operator, so `low(main)` for `.func low(n) = .lobyte(n)` names nothing wide.
            case CallExpressionSyntax { Callee: { } callee } call
                when model.SymbolOf(callee, on) is { Kind: SymbolKind.Func, Items: [var body, ..] } function:
                if (!visiting.Add(function))
                    return false;
                try
                {
                    if (NamesWide(model, body, on, bytes, placed, visiting))
                        return true;
                    if (FunctionArguments.Match(function, call) is not { } arguments)
                        return call.Arguments.ChildNodes.Any(part => NamesWide(model, part, on, bytes, placed, visiting));
                    for (var i = 0; i < arguments.Count; i++)
                    {
                        if (NamesWide(model, arguments[i], on, bytes, placed, visiting)
                            && Exposes(model, body, function.ParameterSymbols[i], on, []))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                finally
                {
                    visiting.Remove(function);
                }

            // The output writes `.loword`, `.hiword` and `.endof` around the names they are given,
            // and a `.select` or a `.switch` as the value it chooses. Every other built-in is
            // written as its value or stands for no address. A word of an address fits a slot of
            // two bytes.
            case CallExpressionSyntax { Callee: null, BuiltinKind: BuiltinKind.Loword or BuiltinKind.Hiword } when bytes >= 2:
                return false;
            case CallExpressionSyntax { Callee: null, BuiltinKind: BuiltinKind.Loword or BuiltinKind.Hiword or BuiltinKind.Endof } builtin:
                return builtin.Arguments.ChildNodes.Any(argument => NamesWide(model, argument, on, bytes, placed, visiting));
            case CallExpressionSyntax { Callee: null } chooser:
                return model.ChosenBy(chooser, on) is { } chosen && NamesWide(model, chosen, on, bytes, placed, visiting);

            case ParenthesizedExpressionSyntax or UnaryExpressionSyntax or BinaryExpressionSyntax or ArgumentListSyntax:
                return node.ChildNodes.Any(child => NamesWide(model, child, on, bytes, placed, visiting));

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns whether an address of address size <paramref name="size"/> and linked range
    /// <paramref name="range"/> may be refused in a slot of <paramref name="bytes"/> bytes. ca65
    /// refuses one wider than the slot. ld65 refuses one placed past the slot's reach, which
    /// counts where <paramref name="placed"/> says so. An address nt65 cannot bound is anywhere
    /// its address size reaches.
    /// </summary>
    private static bool Wider(AddressSize size, (long Low, long High)? range, int bytes, bool placed) =>
        (int)size > bytes || (placed && !Fits(range ?? Reach(size), bytes));

    /// <summary>Returns the addresses an address of address size <paramref name="size"/> reaches.</summary>
    private static (long Low, long High)? Reach(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => (0, 0xff),
        AddressSize.Absolute => (0, 0xffff),
        AddressSize.Far => (0, 0xffffff),
        _ => null,
    };

    /// <summary>
    /// Returns the range of values the address <paramref name="symbol"/>, of address size
    /// <paramref name="size"/>, may take once linked, or null where nt65 cannot bound it. A label
    /// is inside the memory areas the linked configurations run its segment in, as
    /// <c>.runof</c> of the segment is. A label in a segment placed in bank <c>$7e</c> is
    /// therefore <c>$7e0000</c> or more, whatever its address size. Where the configurations do
    /// not bound the segment, an address is anywhere its address size reaches. So is a name given
    /// an address with <c>=</c>, which need not be in the segment it is declared in.
    /// </summary>
    /// <remarks>
    /// An absolute label is in the bank its area starts in, and a zero-page label in that page,
    /// even where the area runs on past it. An area such as <c>$e000</c> with <c>$3f00</c> bytes
    /// is common, and on the 6502 ld65 would refuse any absolute operand that named a label past
    /// the bank.
    /// </remarks>
    private static (long Low, long High)? AddressRange(SemanticModel model, Symbol symbol, AddressSize size)
    {
        if (symbol is not { ValueExpression: null, Segment: { } name } || model.Segments.Find(name) is not { } segment
            || SegmentFunctions.PlacedRange(segment.Runs, segment) is null)
        {
            return Reach(size);
        }
        long? reach = size switch
        {
            AddressSize.ZeroPage => 0xff,
            AddressSize.Absolute => 0xffff,
            _ => null,
        };
        return (segment.Runs.Min(area => area.First),
            segment.Runs.Max(area => reach is { } last ? Math.Min(area.Last, area.First | last) : area.Last));
    }

    /// <summary>
    /// Returns whether part of a <c>.func</c> body uses <paramref name="parameter"/> outside every
    /// byte operator, directly or through a function it passes the parameter to. Only such a use
    /// makes the address size of the argument matter to ca65.
    /// </summary>
    /// <param name="model">The model the body is read in.</param>
    /// <param name="node">The part of the body to search.</param>
    /// <param name="parameter">The parameter to look for.</param>
    /// <param name="on">The expansion the call is in.</param>
    /// <param name="visiting">The functions already entered, so that a recursive body ends.</param>
    private static bool Exposes(SemanticModel model, SyntaxNode node, Symbol parameter, Expansion? on, HashSet<Symbol> visiting)
    {
        switch (node)
        {
            case UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret }:
            case CallExpressionSyntax { Callee: null, BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte }:
                return false;
            case NameExpressionSyntax name:
                return model.SymbolOf(name, on) == parameter;
            case CallExpressionSyntax { Callee: { } callee } call
                when model.SymbolOf(callee, on) is { Kind: SymbolKind.Func, Items: [var body, ..] } function
                && FunctionArguments.Match(function, call) is { } arguments:
                if (!visiting.Add(function))
                    return false;
                try
                {
                    for (var i = 0; i < arguments.Count; i++)
                    {
                        if (Exposes(model, arguments[i], parameter, on, visiting)
                            && Exposes(model, body, function.ParameterSymbols[i], on, visiting))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                finally
                {
                    visiting.Remove(function);
                }
            default:
                return node.ChildNodes.Any(child => Exposes(model, child, parameter, on, visiting));
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
                if (!symbol.IsAddress || model.AddressSizeOf(name, null, on) is not { } size)
                    return null;
                return AddressRange(model, symbol, size);

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

            // A segment function is bounded by where the linked configurations place the segment.
            case CallExpressionSyntax segmental when SegmentFunctions.Of(segmental, model) is { } about:
                return SegmentFunctions.RangeOf(about.Function, about.Segment);

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
