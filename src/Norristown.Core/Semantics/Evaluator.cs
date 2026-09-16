using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Works out what expressions are worth, and with them each symbol's kind, value
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
    private readonly Dictionary<Symbol, Value> arguments = [];

    // Names an `.each` bound to a list item, which stand for the item wherever they are
    // written rather than only for what it is worth.
    private readonly Dictionary<Symbol, SyntaxNode> items = [];

    // What a macro parameter was given, for the built-ins that ask about the argument rather
    // than about its value.
    private readonly Dictionary<Symbol, MacroArgument> given = [];
    private readonly Func<string, long?>? binaryLength;

    private Evaluator(
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic>? diagnostics,
        Func<string, long?>? binaryLength = null,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null)
    {
        this.segments = segments;
        this.resolved = resolved;
        this.diagnostics = diagnostics;
        this.binaryLength = binaryLength;

        // A name a repetition binds stands for its value on this turn, which is what a
        // function's parameter already does for its argument.
        foreach (var (symbol, value) in bound ?? new Dictionary<Symbol, Expansion.Bound>())
        {
            if (value.Argument is { } argument)
                given[symbol] = argument;
            if (value.Item is { } item)
                items[symbol] = item;
            else
                arguments[symbol] = value.Value;
        }
    }

    /// <summary>
    /// Gives every symbol in <paramref name="symbols"/> its kind, value and address size,
    /// reporting what it finds wrong into <paramref name="diagnostics"/>.
    /// </summary>
    public static void EvaluateSymbols(
        SegmentTable segments,
        IReadOnlyList<Symbol> symbols,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic> diagnostics,
        Func<string, long?>? binaryLength = null)
    {
        var evaluator = new Evaluator(segments, resolved, diagnostics, binaryLength);
        foreach (var symbol in symbols)
            evaluator.EvaluateSymbol(symbol);
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> and reports what is wrong with it. An
    /// expression that is nobody's value — an operand of a data directive — is never reached
    /// by the pass over the symbols, so whoever reads it asks for it to be checked.
    /// </summary>
    public static void Check(
        SyntaxNode expression,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic> diagnostics,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, diagnostics, binaryLength, bound).Bytes(expression);

    /// <summary>Evaluates an operand for its bytes, or for its value when it has no bytes.</summary>
    private void Bytes(SyntaxNode operand)
    {
        if (BytesIn(operand) is null)
            Evaluate(operand);
    }

    /// <summary>How much room a data directive takes, for a caller that has already reported.</summary>
    public static DataSize? DataSizeOf(
        SyntaxNode directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, binaryLength, bound).RoomFor(directive);

    /// <summary>The bytes a literal or a mapped string becomes, or null for anything else.</summary>
    public static IReadOnlyList<long>? BytesOf(
        SyntaxNode argument,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).BytesIn(argument);

    /// <summary>The symbol a written name stands for, or null when it names none.</summary>
    public static Symbol? SymbolNamed(
        SyntaxNode name,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved) =>
        name.Kind == SyntaxKind.NameExpression
            ? new Evaluator(SegmentTable.Standard, resolved, null).SymbolOf(name)
            : null;

    /// <summary>The items a name stands for when it names a list, or null when it does not.</summary>
    public static IReadOnlyList<SyntaxNode>? ItemsOf(
        SyntaxNode argument,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved) =>
        argument.Kind == SyntaxKind.NameExpression
        && new Evaluator(SegmentTable.Standard, resolved, null).SymbolOf(argument) is { Kind: SymbolKind.List } list
            ? list.Items
            : null;

    /// <summary>
    /// What an expression is worth once every symbol has been evaluated. Nothing is reported
    /// from here: this answers a question an editor asked, about a file that has already had
    /// everything wrong with it reported.
    /// </summary>
    public static Value ValueOf(
        SyntaxNode expression,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).Evaluate(expression);

    /// <summary>The address size of an expression, with <paramref name="segment"/> giving <c>*</c> its size.</summary>
    public static AddressSize? AddressSizeOf(
        SyntaxNode expression,
        string segment,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).SizeOf(expression, segment);

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

        switch (symbol.Kind)
        {
            // A layout assigns its members their offsets and sizes, and takes its own size
            // from them; asking a member first asks the type that holds it.
            case SymbolKind.Struct:
            case SymbolKind.Union:
                evaluating.Add(symbol);
                LayOut(symbol);
                evaluating.RemoveAt(evaluating.Count - 1);
                return;
            case SymbolKind.Member:
                if (symbol.Scope.Owner is { } holder)
                {
                    evaluated.Remove(symbol);
                    EvaluateSymbol(holder);
                }
                return;
            case SymbolKind.List:
                symbol.Count = symbol.Items.Count;
                return;
            case SymbolKind.Constant when symbol.FollowsPrevious:
                symbol.Value = Value.Of(Follows(symbol));
                return;
            default:
                break;
        }

        // A label or an instance takes its size and its element count from the directive it
        // was written on, which is what `.sizeof` and `.countof` answer for it.
        symbol.Type ??= Constructs.TagTypeOf(symbol.Data) is { } tagged ? SymbolOf(tagged) : null;
        if (symbol.Data is not null && RoomFor(symbol.Data) is { } room)
        {
            symbol.Size = room.Bytes;
            symbol.Count = room.Elements;
        }

        if (symbol.ValueExpression is not { } expression)
        {
            // A label, a routine or a scope: its address is where it lands, which only the
            // linker knows, and its size comes from the segment it sits in.
            symbol.AddressSize = symbol.Kind switch
            {
                SymbolKind.Label or SymbolKind.Proc or SymbolKind.Instance => SegmentSize(symbol.Segment),
                _ => symbol.AddressSize,
            };
            return;
        }

        evaluating.Add(symbol);
        symbol.Value = Evaluate(expression);
        evaluating.RemoveAt(evaluating.Count - 1);

        // `NAME = expr` is a constant if the expression names no address, and an address
        // alias if it does. Imports and extern procs are already classified.
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
                return ValueOfName(node);

            case SyntaxKind.UnaryExpression:
                return Child(node) is { } operand ? Unary(node.ChildTokens[0], Evaluate(operand)) : Value.Unknown;

            case SyntaxKind.BinaryExpression:
                var children = node.ChildNodes;
                if (children.Length != 2 || node.ChildTokens.Length == 0)
                    return Value.Unknown;

                // `&&` and `||` leave the right operand alone once the left decides the
                // result, so `.defined(TRACE) && TRACE` is answerable when TRACE is not
                // defined and the name on the right is never looked up.
                var op = node.ChildTokens[0];
                var first = Evaluate(children[0]);
                if (first.AsNumber() is { } decided && Operators.ShortCircuits(op.Kind, decided))
                    return Value.Of(decided != 0);
                var second = Evaluate(children[1]);

                // A `one` parameter and a repetition over words compare as words: the side
                // that is not already one is the bare name written beside it, which is a word
                // rather than a name and is never looked up (§11.2).
                if (op.Kind is SyntaxKind.EqualsEquals or SyntaxKind.BangEquals
                    && (first.IsWord || second.IsWord)
                    && WordOf(first, children[0]) is { } left && WordOf(second, children[1]) is { } right)
                {
                    var same = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                    return Value.Of(op.Kind == SyntaxKind.EqualsEquals ? same : !same);
                }
                return Binary(op, first, second);

            case SyntaxKind.CallExpression:
                return Call(node);

            // `*`, an error the parser has already reported, and the CPU names, which only
            // `.cpu` and `.target` accept.
            default:
                return Value.Unknown;
        }
    }

    /// <summary>
    /// What a written name is worth. Inside a function body a parameter stands for the
    /// argument it was called with; a member reached through a path is the offsets along
    /// that path added up, which is what makes `Player::pos::y` a number.
    /// </summary>
    private Value ValueOfName(SyntaxNode name)
    {
        if (BoundItem(name) is { } item)
            return Evaluate(item);
        if (SymbolOf(name) is not { } symbol)
            return Value.Unknown;
        if (arguments.TryGetValue(symbol, out var argument))
            return argument;
        return symbol.Kind == SymbolKind.Member ? OffsetAlong(name) : ValueOfSymbol(symbol);
    }

    /// <summary>
    /// The offset a path of members comes to. A type contributes nothing and a member its
    /// own offset; a path that starts at an instance is an address, which only the linker
    /// knows, so it has no value here and is written symbolically instead.
    /// </summary>
    private Value OffsetAlong(SyntaxNode name)
    {
        long offset = 0;
        foreach (var token in name.ChildTokens)
        {
            if (!resolved.TryGetValue((name.Tree, token.Span.Start), out var part))
                continue;
            if (part.IsAddress)
                return Value.Unknown;
            if (part.Kind != SymbolKind.Member)
                continue;
            EvaluateSymbol(part);
            if (part.Value.AsNumber() is not { } own)
                return Value.Unknown;
            offset += own;
        }
        return Value.Of(offset);
    }

    /// <summary>An enum member with no value of its own: the one before it plus one, from zero.</summary>
    private long Follows(Symbol member)
    {
        if (member.PreviousMember is not { } previous)
            return 0;
        EvaluateSymbol(previous);
        return (previous.Value.AsNumber() ?? 0) + 1;
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
        return Operators.Unary(op.Kind, value) is { } result ? Value.Of(result) : Value.Unknown;
    }

    /// <summary>
    /// The word one side of a comparison stands for: the value when it is already a word, or
    /// else the bare name written there, which a word is compared against unlooked-up.
    /// </summary>
    private static string? WordOf(Value value, SyntaxNode written) => value.IsWord
        ? value.Text
        : written is { Kind: SyntaxKind.NameExpression, ChildTokens.Length: 1 }
            ? written.ChildTokens[0].Text
            : null;

    private Value Binary(SyntaxToken op, Value left, Value right)
    {
        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
            return Reject(op, left.IsString ? left : right);
        if (b == 0 && Operators.Divides(op.Green))
        {
            Report(op, "division by zero");
            return Value.Unknown;
        }
        return Operators.Binary(op.Green, a, b) is { } result ? Value.Of(result) : Value.Unknown;
    }

    /// <summary>An operand that is a string where a number belongs; there is no string arithmetic.</summary>
    private Value Reject(SyntaxToken op, Value operand)
    {
        if (operand.IsString)
            Report(op, $"`{op.Text}` cannot be used on a string");
        return Value.Unknown;
    }

    /// <summary>
    /// A call: a built-in function, a character mapping applied to text, or a function
    /// declared with <c>.func</c>, which means its body with the arguments in place of its
    /// parameters.
    /// </summary>
    private Value Call(SyntaxNode call)
    {
        var given = call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? [];
        if (call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.NameExpression) is { } callee)
            return Applied(callee, given);
        if (call.ChildTokens.Length == 0 || call.ChildTokens[0].Kind != SyntaxKind.Directive)
            return Value.Unknown;

        var name = call.ChildTokens[0].Text.ToLowerInvariant();
        var arguments = given;

        // What a macro body adds asks about an argument rather than about a value, so each
        // reads the binding rather than evaluating what is written.
        if (name is ".mode" or ".empty")
        {
            if (arguments.Length != 1 || Argument(arguments[0]) is not { } about)
                return Value.Unknown;
            return name == ".mode"
                ? about.Operand is { } operand ? Value.Word(Operands.ModeOf(operand)) : Value.Unknown
                : Value.Of(about.Block is null || Macros.LinesOf(about.Block).Count == 0);
        }

        if (name is ".sizeof" or ".countof")
        {
            if (arguments.Length != 1 || SymbolOf(arguments[0]) is not { } measured)
                return Value.Unknown;

            // `.countof(p)` of a `list` parameter is how many arguments the call gave it.
            if (name == ".countof" && Argument(arguments[0]) is { Parameter.Kind: ParameterKind.List } listed)
                return Value.Of(listed.Items.Count);
            EvaluateSymbol(measured);
            var room = name == ".sizeof" ? measured.Size : measured.Count;
            return room is { } number ? Value.Of(number) : Value.Unknown;
        }

        // `.addrsize` asks about the shape of its argument rather than its value.
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

    /// <summary>What a name's macro parameter was given, or null when it names no parameter.</summary>
    private MacroArgument? Argument(SyntaxNode name) =>
        SymbolOf(name) is { } symbol ? given.GetValueOrDefault(symbol) : null;

    /// <summary>
    /// A charmap or a function called by name. A charmap maps one character to its byte; a
    /// function evaluates its body with each parameter bound to the argument it was given,
    /// and a function that ends up needing itself is the same cycle any constant would be.
    /// </summary>
    private Value Applied(SyntaxNode callee, IReadOnlyList<SyntaxNode> given)
    {
        if (SymbolOf(callee) is not { } symbol)
            return Value.Unknown;

        if (symbol.Kind == SymbolKind.Charmap)
        {
            var mapped = Map(symbol);
            return given.Count == 1 && Evaluate(given[0]) is { Kind: ValueKind.Number } character
                && mapped.TryGetValue((int)character.Number, out var b)
                ? Value.Of(b)
                : Value.Unknown;
        }

        if (symbol.Kind != SymbolKind.Func || symbol.Items.Count == 0)
            return Value.Unknown;
        if (symbol.ParameterSymbols.Count != given.Count)
        {
            Report(callee, $"`{symbol.Name}` takes {symbol.ParameterSymbols.Count} argument(s), "
                + $"and {given.Count} were given");
            return Value.Unknown;
        }
        if (evaluating.Contains(symbol))
        {
            ReportCycle(evaluating.IndexOf(symbol));
            return Value.Unknown;
        }

        var values = given.Select(Evaluate).ToArray();
        var shadowed = new List<(Symbol Symbol, Value Value, bool Had)>();
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = symbol.ParameterSymbols[i];
            shadowed.Add((parameter, arguments.GetValueOrDefault(parameter), arguments.ContainsKey(parameter)));
            arguments[parameter] = values[i];
        }

        evaluating.Add(symbol);
        var result = Evaluate(symbol.Items[0]);
        evaluating.RemoveAt(evaluating.Count - 1);
        foreach (var (parameter, previous, had) in shadowed)
        {
            if (had)
                arguments[parameter] = previous;
            else
                arguments.Remove(parameter);
        }
        return result;
    }

    /// <summary>
    /// A charmap read into the mapping it describes. Each entry gives one character or a
    /// range of them consecutive values; a character no entry names has no byte, and using
    /// the mapping on it is an error where it is used.
    /// </summary>
    private Dictionary<int, long> Map(Symbol charmap)
    {
        var mapped = new Dictionary<int, long>();
        foreach (var entry in charmap.Entries)
        {
            var parts = entry.ChildNodes;
            if (parts.Length < 2)
                continue;
            var first = Evaluate(parts[0]).AsNumber();
            var last = parts.Length > 2 ? Evaluate(parts[1]).AsNumber() : first;
            var to = Evaluate(parts[^1]).AsNumber();
            if (first is null || last is null || to is null || last < first)
                continue;
            for (var c = first.Value; c <= last.Value; c++)
                mapped[(int)c] = to.Value + (c - first.Value);
        }
        return mapped;
    }

    private static Value Number1(Value[] arguments, Func<long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a] ? Value.Of(apply(a.Number)) : Value.Unknown;

    private static Value Number2(Value[] arguments, Func<long, long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a, { Kind: ValueKind.Number } b]
            ? Value.Of(apply(a.Number, b.Number))
            : Value.Unknown;

    /// <summary>
    /// The address size of an expression: a constant's value decides, and otherwise
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

    /// <summary>
    /// How much room a data directive takes: the bytes it generates, and how many elements
    /// they are. Null where nt65 cannot say — an `.align`, whose size depends on where it
    /// lands, or a directive whose operands do not add up.
    /// </summary>
    private DataSize? RoomFor(SyntaxNode directive)
    {
        if (directive.ChildTokens.Length == 0)
            return null;
        var name = directive.ChildTokens[0].Text.ToLowerInvariant();
        var operands = directive.ChildNodes;

        switch (name)
        {
            case ".byte":
            case ".lobytes":
            case ".hibytes":
                return Spread(operands, width: 1);
            case ".word":
            case ".addr":
                return Spread(operands, width: 2);
            case ".faraddr":
                return Spread(operands, width: 3);
            case ".dword":
                return Spread(operands, width: 4);

            // `.asciiz` is the text and the zero byte that ends it.
            case ".asciiz":
                return Spread(operands, width: 1) is { } text
                    ? new DataSize(text.Bytes + 1, text.Elements + 1)
                    : null;

            case ".res":
                return operands.Length > 0 && Evaluate(operands[0]).AsNumber() is { } reserved and >= 0
                    ? new DataSize(reserved, reserved)
                    : null;

            case ".tag":
                return RoomForTag(operands);

            case ".incbin":
                return RoomForBinary(directive, operands);

            // An `.align` generates however many bytes it takes to reach the next boundary,
            // which depends on where it lands, so it has no size of its own.
            default:
                return null;
        }
    }

    /// <summary>
    /// How much room a struct or union member takes. A member reserves one element rather
    /// than emitting values, so a bare `.word` is two bytes where in data it would be none.
    /// </summary>
    private DataSize? RoomForMember(SyntaxNode? directive)
    {
        if (directive is not { ChildTokens.Length: > 0 })
            return null;
        return directive.ChildTokens[0].Text.ToLowerInvariant() switch
        {
            ".byte" => new DataSize(1, 1),
            ".word" or ".addr" => new DataSize(2, 1),
            ".faraddr" => new DataSize(3, 1),
            ".dword" => new DataSize(4, 1),
            ".res" or ".tag" => RoomFor(directive),
            _ => null,
        };
    }

    /// <summary>
    /// One element per operand, except that text is one element per byte and a list stands
    /// for its own items.
    /// </summary>
    private DataSize Spread(IReadOnlyList<SyntaxNode> operands, int width)
    {
        long elements = 0;
        foreach (var operand in operands)
        {
            if (BytesIn(operand) is { } bytes)
                elements += bytes.Count;
            else if (SymbolOf(operand) is { Kind: SymbolKind.List } list)
                elements += list.Items.Count;
            else
                elements++;
        }
        return new DataSize(elements * width, elements);
    }

    /// <summary>An instance of a type, or an array of them, or one written out with values.</summary>
    private DataSize? RoomForTag(IReadOnlyList<SyntaxNode> operands)
    {
        if (operands.Count == 0 || SymbolOf(operands[0]) is not { } type)
            return null;
        EvaluateSymbol(type);
        if (!type.IsLayout || type.Size is not { } stride)
            return null;

        // `.tag T { ... }` is one instance whose values are written out; `.tag T, n` is n of
        // them; `.tag T` on its own is one.
        var count = operands.Count > 1 && operands[1].Kind != SyntaxKind.TagValues
            ? Evaluate(operands[1]).AsNumber()
            : 1;
        return count is { } many and >= 0 ? new DataSize(stride * many, many) : null;
    }

    /// <summary>
    /// A binary file, whose length nt65 reads for itself. The path is relative to the file
    /// that names it, and an offset and a length may narrow it.
    /// </summary>
    private DataSize? RoomForBinary(SyntaxNode directive, IReadOnlyList<SyntaxNode> operands)
    {
        if (operands.Count == 0 || Evaluate(operands[0]) is not { Kind: ValueKind.String, Text: { } path })
            return null;

        var from = directive.Tree.Path.LastIndexOf('/') is var at && at >= 0
            ? directive.Tree.Path[..(at + 1)] + path
            : path;
        if (binaryLength?.Invoke(from) is not { } length)
        {
            Report(directive, $"`{path}` cannot be read");
            return null;
        }

        var offset = operands.Count > 1 ? Evaluate(operands[1]).AsNumber() ?? 0 : 0;
        var taken = operands.Count > 2 ? Evaluate(operands[2]).AsNumber() : null;
        var available = Math.Max(0, length - offset);
        var bytes = taken is { } wanted ? Math.Min(wanted, available) : available;
        return new DataSize(bytes, bytes);
    }

    /// <summary>
    /// The bytes an operand becomes: a string or character literal is its characters, and a
    /// charmap applied to one is what the mapping gives them. Null for anything else.
    /// </summary>
    private IReadOnlyList<long>? BytesIn(SyntaxNode operand)
    {
        if (operand.Kind is SyntaxKind.StringExpression or SyntaxKind.CharacterExpression)
        {
            var value = Evaluate(operand);
            return value.Kind switch
            {
                ValueKind.String => [.. value.Text!.Select(c => (long)c)],
                ValueKind.Number => [value.Number],
                _ => null,
            };
        }

        if (operand.Kind != SyntaxKind.CallExpression
            || operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.NameExpression) is not { } callee
            || SymbolOf(callee) is not { Kind: SymbolKind.Charmap } charmap)
        {
            return null;
        }

        var given = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? [];
        if (given.Length != 1)
            return null;
        var text = Evaluate(given[0]);
        var characters = text.Kind switch
        {
            ValueKind.String => text.Text!.Select(c => (int)c),
            ValueKind.Number => [(int)text.Number],
            _ => (IEnumerable<int>)[],
        };

        var mapped = Map(charmap);
        var bytes = new List<long>();
        foreach (var character in characters)
        {
            if (!mapped.TryGetValue(character, out var b))
            {
                Report(operand, $"`{charmap.Name}` does not map `{(char)character}`");
                return null;
            }
            bytes.Add(b);
        }
        return bytes;
    }

    /// <summary>
    /// A structure or a union, and the members it holds: each gets its offset and its size,
    /// and the type takes its own size from them. A union puts every member at zero and is
    /// as big as its largest.
    /// </summary>
    private void LayOut(Symbol type)
    {
        long offset = 0;
        long largest = 0;
        long members = 0;
        foreach (var member in type.Body?.Symbols ?? [])
        {
            if (member.Kind != SymbolKind.Member)
                continue;
            // The type a member names is worth keeping on it: emission walks into it, and
            // nothing else would have resolved it unless a path happened to reach through.
            member.Type ??= Constructs.TagTypeOf(member.Data) is { } named ? SymbolOf(named) : null;
            var room = RoomForMember(member.Data);
            if (room is null)
            {
                Report(member.DeclarationSpan, $"`{member.Name}` reserves no room a member may take", []);
                continue;
            }

            member.Value = Value.Of(type.Kind == SymbolKind.Union ? 0 : offset);
            member.Size = room.Value.Bytes;
            member.Count = room.Value.Elements;
            evaluated.Add(member);
            if (type.Kind == SymbolKind.Union)
                largest = Math.Max(largest, room.Value.Bytes);
            else
                offset += room.Value.Bytes;
            members++;
        }
        type.Size = type.Kind == SymbolKind.Union ? largest : offset;
        type.Count = members;
    }

    private AddressSize? SegmentSize(string? segment) =>
        segment is null ? null : segments.Find(segment)?.Size;

    /// <summary>
    /// What a name resolved to: the last part of the path, which is what it stands for. The
    /// file is part of the key, because following a name into another file lands on offsets
    /// that mean something else there.
    /// </summary>
    private Symbol? SymbolOf(SyntaxNode name)
    {
        // A name bound to a list item is that item: `.each handlers, h` makes `h` the label
        // it stands for, with that label's address size and everything else about it.
        if (BoundItem(name) is { Kind: SyntaxKind.NameExpression } item)
            return SymbolOf(item);

        for (var i = name.ChildTokens.Length - 1; i >= 0; i--)
        {
            if (resolved.TryGetValue((name.Tree, name.ChildTokens[i].Span.Start), out var symbol))
                return symbol;
        }
        return null;
    }

    /// <summary>The item a written name is bound to on this turn, or null when it is bound to none.</summary>
    private SyntaxNode? BoundItem(SyntaxNode name)
    {
        if (items.Count == 0 || name.ChildTokens.Length != 1)
            return null;
        return resolved.TryGetValue((name.Tree, name.ChildTokens[0].Span.Start), out var symbol)
            && items.TryGetValue(symbol, out var item)
            ? item
            : null;
    }

    private static string Text(SyntaxNode node) => node.ChildTokens.Length > 0 ? node.ChildTokens[0].Text : "";

    private static SyntaxNode? Child(SyntaxNode node) => node.ChildNodes.Length > 0 ? node.ChildNodes[0] : null;

    private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

    private void Report(Span span, string message, IReadOnlyList<RelatedSpan> related) =>
        diagnostics?.Add(new Diagnostic(span, Severity.Error, message, related));

    private void Report(SyntaxToken token, string message) =>
        diagnostics?.Add(new Diagnostic(token.Parent.Tree.GetSpan(token.Span), Severity.Error, message, []));

    private void Report(SyntaxNode node, string message) =>
        diagnostics?.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message, []));
}
