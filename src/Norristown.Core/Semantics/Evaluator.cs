using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Works out what the expressions of §9 are worth, and with them each symbol's kind, value
/// and address size.
/// <para>
/// A constant may be written before the names it uses, so evaluation follows references
/// rather than the order of the file, and a name that ends up needing itself is an error
/// reported once for the whole cycle. Everything built only from constants gets a value;
/// an expression naming an address gets none, and is emitted symbolically for ca65 to
/// resolve.
/// </para>
/// </summary>
internal sealed class Evaluator
{
    private readonly SegmentTable segments;
    private readonly IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved;
    private readonly List<Diagnostic>? diagnostics;
    private readonly HashSet<Symbol> evaluated = [];
    private readonly List<Symbol> evaluating = [];

    private Evaluator(
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic>? diagnostics)
    {
        this.segments = segments;
        this.resolved = resolved;
        this.diagnostics = diagnostics;
    }

    /// <summary>
    /// Gives every symbol in <paramref name="symbols"/> its kind, value and address size,
    /// reporting what it finds wrong into <paramref name="diagnostics"/>.
    /// </summary>
    public static void EvaluateSymbols(
        SegmentTable segments,
        IReadOnlyList<Symbol> symbols,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic> diagnostics)
    {
        var evaluator = new Evaluator(segments, resolved, diagnostics);
        foreach (var symbol in symbols)
            evaluator.EvaluateSymbol(symbol);
    }

    /// <summary>
    /// What an expression is worth once every symbol has been evaluated. Nothing is reported
    /// from here: this answers a question an editor asked, about a file that has already had
    /// everything wrong with it reported.
    /// </summary>
    public static Value ValueOf(
        SyntaxNode expression,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved) =>
        new Evaluator(segments, resolved, null).Evaluate(expression);

    /// <summary>The address size of an expression (§7.2), with <paramref name="segment"/> giving <c>*</c> its size.</summary>
    public static AddressSize? AddressSizeOf(
        SyntaxNode expression,
        string segment,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved) =>
        new Evaluator(segments, resolved, null).SizeOf(expression, segment);

    /// <summary>The wider of two address sizes, either of which may be unknown.</summary>
    private static AddressSize? Widest(AddressSize? a, AddressSize? b) =>
        a is null ? b : b is null ? a : (AddressSize)Math.Max((int)a, (int)b);

    private void EvaluateSymbol(Symbol symbol)
    {
        var index = evaluating.IndexOf(symbol);
        if (index >= 0)
        {
            ReportCycle(index);
            return;
        }
        if (!evaluated.Add(symbol))
            return;

        if (symbol.ValueExpression is not { } expression)
        {
            // A label, a routine or a scope: its address is where it lands, which only the
            // linker knows, and its size comes from the segment it sits in (§7.2).
            symbol.AddressSize = symbol.Kind switch
            {
                SymbolKind.Label or SymbolKind.Proc => SegmentSize(symbol.Segment),
                _ => symbol.AddressSize,
            };
            return;
        }

        evaluating.Add(symbol);
        symbol.Value = Evaluate(expression);
        evaluating.RemoveAt(evaluating.Count - 1);

        // `NAME = expr` is a constant if the expression names no address, and an address
        // alias if it does (§6.1). Imports and extern procs are already classified.
        if (symbol.Kind == SymbolKind.Constant && NamesAnAddress(expression))
            symbol.Kind = SymbolKind.AddressAlias;
        if (symbol.Kind != SymbolKind.ImportedAddress)
            symbol.AddressSize = SizeOf(expression, symbol.Segment ?? SegmentTable.DefaultSegment, symbol.Value);
    }

    /// <summary>
    /// Reports a cycle once, at the declaration that closes it, naming the rest of the ring.
    /// Every symbol on it is left without a value, and none of them reports again.
    /// </summary>
    private void ReportCycle(int index)
    {
        var ring = evaluating[index..];
        var symbol = ring[0];
        Report(symbol.DeclarationSpan, $"`{symbol.DisplayName}` is defined in terms of itself",
            [.. ring.Skip(1).Select(other =>
                new RelatedSpan(other.DeclarationSpan, $"through `{other.DisplayName}`"))]);
    }

    private Value Evaluate(SyntaxNode node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.NumberExpression:
                return Number(Literals.Number(Text(node)));

            case SyntaxKind.CharacterExpression:
                return Number(Literals.Character(Text(node)));

            case SyntaxKind.StringExpression:
                return Literals.Text(Text(node)) is { } text ? Value.Of(text) : Value.Unknown;

            case SyntaxKind.ParenthesizedExpression:
                return Child(node) is { } inner ? Evaluate(inner) : Value.Unknown;

            case SyntaxKind.NameExpression:
                return SymbolOf(node) is { } symbol ? ValueOfSymbol(symbol) : Value.Unknown;

            case SyntaxKind.UnaryExpression:
                return Child(node) is { } operand ? Unary(node.ChildTokens[0], Evaluate(operand)) : Value.Unknown;

            case SyntaxKind.BinaryExpression:
                var children = node.ChildNodes;
                return children.Length == 2 && node.ChildTokens.Length > 0
                    ? Binary(node.ChildTokens[0], Evaluate(children[0]), Evaluate(children[1]))
                    : Value.Unknown;

            case SyntaxKind.CallExpression:
                return Call(node);

            // `*`, an error the parser has already reported, and the CPU names, which only
            // `.cpu` and `.target` accept.
            default:
                return Value.Unknown;
        }
    }

    /// <summary>
    /// The value a name stands for, evaluating its declaration first if need be. A label has
    /// no value at all: where it lands is the linker's to say.
    /// </summary>
    private Value ValueOfSymbol(Symbol symbol)
    {
        // Only the pass that reports may evaluate, so answering an editor's question never
        // writes to a symbol another thread is reading.
        if (diagnostics is not null)
            EvaluateSymbol(symbol);
        return symbol.Value;
    }

    private Value Unary(SyntaxToken op, Value operand)
    {
        if (operand.AsNumber() is not { } value)
            return Reject(op, operand);
        return op.Kind switch
        {
            SyntaxKind.Plus => Value.Of(value),
            SyntaxKind.Minus => Value.Of(-value),
            SyntaxKind.Tilde => Value.Of(~value),
            SyntaxKind.Bang => Value.Of(value == 0),
            SyntaxKind.Less => Value.Of(value & 0xff),
            SyntaxKind.Greater => Value.Of((value >> 8) & 0xff),
            SyntaxKind.Caret => Value.Of((value >> 16) & 0xff),
            _ => Value.Unknown,
        };
    }

    private Value Binary(SyntaxToken op, Value left, Value right)
    {
        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
            return Reject(op, left.IsString ? left : right);

        switch (op.Kind)
        {
            case SyntaxKind.Slash when b == 0:
            case SyntaxKind.Directive when b == 0:
                Report(op, "division by zero");
                return Value.Unknown;
        }

        return op.Kind switch
        {
            SyntaxKind.Star => Value.Of(a * b),
            SyntaxKind.Slash => Value.Of(a / b),
            SyntaxKind.Directive => Value.Of(a % b),
            SyntaxKind.Plus => Value.Of(a + b),
            SyntaxKind.Minus => Value.Of(a - b),
            SyntaxKind.LessLess => Shift(a, b, left: true),
            SyntaxKind.GreaterGreater => Shift(a, b, left: false),
            SyntaxKind.Less => Value.Of(a < b),
            SyntaxKind.LessEquals => Value.Of(a <= b),
            SyntaxKind.Greater => Value.Of(a > b),
            SyntaxKind.GreaterEquals => Value.Of(a >= b),
            SyntaxKind.EqualsEquals => Value.Of(a == b),
            SyntaxKind.BangEquals => Value.Of(a != b),
            SyntaxKind.Ampersand => Value.Of(a & b),
            SyntaxKind.Caret => Value.Of(a ^ b),
            SyntaxKind.Bar => Value.Of(a | b),
            SyntaxKind.AmpersandAmpersand => Value.Of(a != 0 && b != 0),
            SyntaxKind.CaretCaret => Value.Of((a != 0) ^ (b != 0)),
            SyntaxKind.BarBar => Value.Of(a != 0 || b != 0),
            _ => Value.Unknown,
        };
    }

    /// <summary>A shift by more than the width of a value says nothing, so it has no value.</summary>
    private static Value Shift(long value, long places, bool left) =>
        places is < 0 or > 63 ? Value.Unknown : Value.Of(left ? value << (int)places : value >> (int)places);

    /// <summary>An operand that is a string where a number belongs (§9 has no string arithmetic).</summary>
    private Value Reject(SyntaxToken op, Value operand)
    {
        if (operand.IsString)
            Report(op, $"`{op.Text}` cannot be used on a string");
        return Value.Unknown;
    }

    /// <summary>The built-in functions of §9 that need nothing beyond an expression's value.</summary>
    private Value Call(SyntaxNode call)
    {
        if (call.ChildTokens.Length == 0 || call.ChildTokens[0].Kind != SyntaxKind.Directive)
            return Value.Unknown;

        var name = call.ChildTokens[0].Text.ToLowerInvariant();
        var arguments = call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? [];

        // `.addrsize` asks about the shape of its argument rather than its value (§7.2).
        if (name == ".addrsize")
        {
            return arguments.Length == 1 && SizeOf(arguments[0], SegmentTable.DefaultSegment) is { } size
                ? Value.Of((long)size)
                : Value.Unknown;
        }

        var values = arguments.Select(Evaluate).ToArray();
        return name switch
        {
            ".lobyte" => Number1(values, v => v & 0xff),
            ".hibyte" => Number1(values, v => (v >> 8) & 0xff),
            ".bankbyte" => Number1(values, v => (v >> 16) & 0xff),
            ".loword" => Number1(values, v => v & 0xffff),
            ".hiword" => Number1(values, v => (v >> 16) & 0xffff),
            ".min" => Number2(values, Math.Min),
            ".max" => Number2(values, Math.Max),
            ".strlen" => values is [{ Kind: ValueKind.String, Text: { } s }] ? Value.Of(s.Length) : Value.Unknown,
            ".strat" => values is [{ Kind: ValueKind.String, Text: { } t }, { Kind: ValueKind.Number } at]
                && at.Number >= 0 && at.Number < t.Length
                ? Value.Of(t[(int)at.Number])
                : Value.Unknown,

            // `.sizeof`, `.countof`, `.endof`, `.spanof`, `.target`, `.defined` and the three
            // a macro body adds arrive with the stages that give them something to measure.
            _ => Value.Unknown,
        };
    }

    private static Value Number1(Value[] arguments, Func<long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a] ? Value.Of(apply(a.Number)) : Value.Unknown;

    private static Value Number2(Value[] arguments, Func<long, long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a, { Kind: ValueKind.Number } b]
            ? Value.Of(apply(a.Number, b.Number))
            : Value.Unknown;

    /// <summary>
    /// The address size of an expression (§7.2): a constant's value decides, and otherwise
    /// the widest of the address symbols it names.
    /// </summary>
    /// <param name="expression">The expression to size.</param>
    /// <param name="segment">The segment <c>*</c> stands in.</param>
    /// <param name="known">
    /// The expression's value where the caller has it, so that sizing an expression does not
    /// evaluate it a second time and report what it found twice.
    /// </param>
    private AddressSize? SizeOf(SyntaxNode expression, string segment, Value? known = null)
    {
        AddressSize? widest = null;
        var named = false;
        Walk(expression);
        return named ? widest : (known ?? Evaluate(expression)).ImpliedAddressSize();

        void Walk(SyntaxNode node)
        {
            if (node.Kind == SyntaxKind.CurrentAddressExpression)
            {
                named = true;
                widest = Widest(widest, SegmentSize(segment));
                return;
            }
            if (node.Kind == SyntaxKind.NameExpression)
            {
                if (SymbolOf(node) is { IsAddress: true } symbol)
                {
                    named = true;
                    widest = Widest(widest, symbol.AddressSize);
                }
                return;
            }
            foreach (var child in node.ChildNodes)
                Walk(child);
        }
    }

    /// <summary>Whether an expression names an address, which is what makes it an alias rather than a constant.</summary>
    private bool NamesAnAddress(SyntaxNode node)
    {
        if (node.Kind == SyntaxKind.CurrentAddressExpression)
            return true;
        if (node.Kind == SyntaxKind.NameExpression)
            return SymbolOf(node) is { IsAddress: true };
        foreach (var child in node.ChildNodes)
        {
            if (NamesAnAddress(child))
                return true;
        }
        return false;
    }

    private AddressSize? SegmentSize(string? segment) =>
        segment is null ? null : segments.Find(segment)?.Size;

    /// <summary>
    /// What a name resolved to: the last part of the path, which is what it stands for. The
    /// file is part of the key, because following a name into another file lands on offsets
    /// that mean something else there (§12).
    /// </summary>
    private Symbol? SymbolOf(SyntaxNode name)
    {
        for (var i = name.ChildTokens.Length - 1; i >= 0; i--)
        {
            if (resolved.TryGetValue((name.Tree, name.ChildTokens[i].Span.Start), out var symbol))
                return symbol;
        }
        return null;
    }

    private static string Text(SyntaxNode node) => node.ChildTokens.Length > 0 ? node.ChildTokens[0].Text : "";

    private static SyntaxNode? Child(SyntaxNode node) => node.ChildNodes.Length > 0 ? node.ChildNodes[0] : null;

    private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

    private void Report(Span span, string message, IReadOnlyList<RelatedSpan> related) =>
        diagnostics?.Add(new Diagnostic(span, Severity.Error, message, related));

    private void Report(SyntaxToken token, string message) =>
        diagnostics?.Add(new Diagnostic(token.Parent.Tree.GetSpan(token.Span), Severity.Error, message, []));
}
