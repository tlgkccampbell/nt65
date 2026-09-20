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

    // The enum member a repetition's name stands for on this turn, which a path ending in
    // that name reaches the member of the same name through.
    private readonly Dictionary<Symbol, Symbol> members = [];

    // What a macro parameter was given, for the built-ins that ask about the argument rather
    // than about its value.
    private readonly Dictionary<Symbol, MacroArgument> given = [];
    private readonly Func<string, long?>? binaryLength;

    // How many bytes a routine or a data declaration takes. Only layout knows, so
    // only a caller that has laid the file out can answer it.
    private readonly Func<Symbol, long?>? spans;

    // For a program in which one file changed: the symbols of the files that did not, whose
    // values stand as they were, and which of them this file's symbols read.
    private readonly Func<Symbol, bool>? settled;
    private readonly HashSet<Symbol> settledReads = [];

    // The file of the symbol being evaluated when each diagnostic was found, alongside them.
    // What a symbol's evaluation finds belongs to its file even when it is found in another
    // file's function body, so a file that is not evaluated again keeps saying it.
    private readonly List<string>? owners;

    // Which branches the build takes, for the conditionals in a data body; without it every
    // condition is worked out as one inside an expansion would be.
    private readonly Configuration? configuration;
    private Symbol? owner;

    // How deep evaluation is inside values `.select` chose, whose names have to mean something,
    // and whether a function body is being read with nothing given, when no choice is known.
    private int choosing;
    private bool readingBody;

    private Evaluator(
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic>? diagnostics,
        Func<string, long?>? binaryLength = null,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Func<Symbol, long?>? spans = null,
        Func<Symbol, bool>? settled = null,
        List<string>? owners = null,
        Configuration? configuration = null)
    {
        this.configuration = configuration;
        this.settled = settled;
        this.owners = owners;
        this.segments = segments;
        this.resolved = resolved;
        this.diagnostics = diagnostics;
        this.binaryLength = binaryLength;
        this.spans = spans;

        // A name a repetition binds stands for its value on this turn, which is what a
        // function's parameter already does for its argument.
        foreach (var (symbol, value) in bound ?? new Dictionary<Symbol, Expansion.Bound>())
        {
            if (value.Argument is { } argument)
                given[symbol] = argument;
            if (value.Member is { } member)
                members[symbol] = member;
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
        Func<string, long?>? binaryLength = null,
        Configuration? configuration = null)
    {
        var evaluator = new Evaluator(segments, resolved, diagnostics, binaryLength, configuration: configuration);
        foreach (var symbol in symbols)
            evaluator.EvaluateSymbol(symbol);
    }

    /// <summary>
    /// The same, saying for each diagnostic which file's symbol found it, in
    /// <paramref name="owners"/>. Where <paramref name="settled"/> says a symbol is settled, its
    /// value is read as it stands rather than worked out again; the ones read are returned.
    /// </summary>
    public static IReadOnlySet<Symbol> EvaluateSymbols(
        SegmentTable segments,
        IReadOnlyList<Symbol> symbols,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic> diagnostics,
        List<string> owners,
        Func<Symbol, bool>? settled,
        Func<string, long?>? binaryLength,
        Configuration? configuration = null)
    {
        var evaluator = new Evaluator(
            segments, resolved, diagnostics, binaryLength, settled: settled, owners: owners, configuration: configuration);
        foreach (var symbol in symbols)
            evaluator.EvaluateSymbol(symbol);
        return evaluator.settledReads;
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
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Func<Symbol, long?>? spans = null) =>
        new Evaluator(segments, resolved, diagnostics, binaryLength, bound, spans).Bytes(expression);

    /// <summary>Evaluates an operand for its bytes, or for its value when it has no bytes.</summary>
    private void Bytes(SyntaxNode operand)
    {
        if (BytesIn(operand) is not null)
            return;

        // A named scope is a namespace and has no address of its own, so nothing is written
        // for it and ca65 would find the name undefined. What a call does with its arguments,
        // `.spanof` of a scope among them, is the call's to say.
        foreach (var node in (IEnumerable<SyntaxNode>)[operand, .. operand.DescendantNodes()])
        {
            if (node is NameExpressionSyntax name && !InsideCall(name, operand)
                && SymbolOf(name) is { Kind: SymbolKind.Scope } scope && name.ChildTokens[^1].Text == scope.Name)
                Report(name, $"`{scope.Name}` is a scope, which has no address: a routine or data inside it does");
        }
        Evaluate(operand);
    }

    private static bool InsideCall(SyntaxNode node, SyntaxNode top)
    {
        for (var at = node.Parent; at is not null && at != top.Parent; at = at.Parent)
        {
            if (at is CallExpressionSyntax)
                return true;
        }
        return false;
    }

    /// <summary>How much room a data directive takes, for a caller that has already reported.</summary>
    public static DataSize? DataSizeOf(
        StatementSyntax directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null) =>
        new Evaluator(segments, resolved, null, binaryLength, bound, configuration: configuration).RoomFor(directive);

    /// <summary>
    /// How many elements an element type declares with its count, and how many its values
    /// come to, either of which may be unknown. The two have to agree.
    /// </summary>
    public static (long? Declared, long? Given) ElementsOf(
        DataDirectiveSyntax directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null)
    {
        var evaluator = new Evaluator(segments, resolved, null, null, bound, configuration: configuration);
        return (evaluator.DeclaredCount(directive), evaluator.GivenCount(directive));
    }

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
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        name is NameExpressionSyntax written
            ? new Evaluator(SegmentTable.Standard, resolved, null, null, bound).SymbolOf(written)
            : null;

    /// <summary>The items a name stands for when it names a list, or null when it does not.</summary>
    public static IReadOnlyList<SyntaxNode>? ItemsOf(
        SyntaxNode argument,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved) =>
        argument is NameExpressionSyntax name
        && new Evaluator(SegmentTable.Standard, resolved, null).SymbolOf(name) is { Kind: SymbolKind.List } list
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
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Func<Symbol, long?>? spans = null) =>
        new Evaluator(segments, resolved, null, null, bound, spans).Evaluate(expression);

    /// <summary>The address size of an expression, with <paramref name="segment"/> giving <c>*</c> its size.</summary>
    public static AddressSize? AddressSizeOf(
        SyntaxNode expression,
        string? segment,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).SizeOf(expression, segment);

    /// <summary>The wider of two address sizes, either of which may be unknown.</summary>
    private static AddressSize? Widest(AddressSize? a, AddressSize? b) =>
        a is null ? b : b is null ? a : (AddressSize)Math.Max((int)a, (int)b);

    private void EvaluateSymbol(Symbol symbol)
    {
        if (settled?.Invoke(symbol) == true)
        {
            settledReads.Add(symbol);
            return;
        }
        var outer = owner;
        owner = symbol;
        EvaluateOwnSymbol(symbol);
        owner = outer;
    }

    private void EvaluateOwnSymbol(Symbol symbol)
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

            // A function has no value of its own, but its body is read once with nothing given,
            // so that functions calling each other in a ring are reported whether or not
            // anything calls them.
            case SymbolKind.Func when symbol.Items.Count > 0 && diagnostics is not null:
                evaluating.Add(symbol);
                var outer = readingBody;
                readingBody = true;
                Evaluate(symbol.Items[0]);
                readingBody = outer;
                evaluating.RemoveAt(evaluating.Count - 1);
                return;
            case SymbolKind.Constant when symbol.FollowsPrevious:
                symbol.Value = Number(Follows(symbol));
                return;
            default:
                break;
        }

        // A data declaration takes its size and its element count from what it declares, which
        // is what `.sizeof` and `.countof` answer for it. Mixed data has bytes and no elements.
        if (symbol.Kind == SymbolKind.Data)
        {
            evaluating.Add(symbol);
            symbol.Type ??= (symbol.Data as DataDirectiveSyntax)?.Type is { } typed ? SymbolOf(typed) : null;
            if (symbol.Data is { } element && RoomFor(element) is { } room)
            {
                symbol.Size = room.Bytes;
                symbol.Count = room.Elements;
            }
            else if (symbol.Definition is BlockSyntax block)
            {
                symbol.Size = RoomForMixed(block);
            }
            evaluating.RemoveAt(evaluating.Count - 1);
        }

        if (symbol.ValueExpression is not { } expression)
        {
            // A label, a routine or a data declaration: its address is where it lands, which
            // only the linker knows, and its size comes from the segment it sits in.
            symbol.AddressSize = symbol.Kind switch
            {
                SymbolKind.Label or SymbolKind.Proc or SymbolKind.Data => SegmentSize(symbol.Segment),
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
        {
            // An enum is a set of numbers, and one that stood for an address would give every
            // member after it a value nothing can work out.
            if (symbol.IsEnumMember)
            {
                Report(expression, $"`{symbol.Name}` is an enum member, whose value is a constant, and this names an address");
                symbol.Value = Value.Unknown;
                return;
            }
            symbol.Kind = SymbolKind.AddressAlias;
        }
        if (symbol.Kind != SymbolKind.ImportedAddress)
            symbol.AddressSize = SizeOf(expression, symbol.Segment, symbol.Value);
    }

    /// <summary>
    /// Reports a cycle once, naming the rest of the ring. Every symbol on it is left without a
    /// value, and none of them reports again. It is reported at the declaration that comes
    /// first in the program, by file and then by position, and the ring is named from there:
    /// which symbol evaluation happened to reach the ring through must not change what is said.
    /// </summary>
    private void ReportCycle(int index)
    {
        var found = evaluating[index..];
        var first = found.IndexOf(found
            .OrderBy(symbol => symbol.Tree.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.NameSpan.Start)
            .First());
        List<Symbol> ring = [.. found.Skip(first), .. found.Take(first)];
        var symbol = ring[0];
        Report(symbol.DeclarationSpan, $"`{symbol.DisplayName}` is defined in terms of itself",
            [.. ring.Skip(1).Select(other =>
                new RelatedSpan(other.DeclarationSpan, $"through `{other.DisplayName}`"))]);
    }

    private Value Evaluate(SyntaxNode node)
    {
        switch (node)
        {
            case NumberExpressionSyntax number:
                return Number(Literals.Number(number.Token.Text));

            case CharacterExpressionSyntax character:
                return Number(Literals.Character(character.Token.Text));

            case StringExpressionSyntax quoted:
                return Literals.Text(quoted.Token.Text) is { } text ? Value.Of(text) : Value.Unknown;

            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression);

            case NameExpressionSyntax name:
                return ValueOfName(name);

            case UnaryExpressionSyntax unary:
                return Unary(unary.OperatorToken, Evaluate(unary.Operand));

            case BinaryExpressionSyntax binary:
                // `&&` and `||` leave the right operand alone once the left decides the
                // result, so `.defined(TRACE) && TRACE` is answerable when TRACE is not
                // defined and the name on the right is never looked up.
                var op = binary.OperatorToken;
                var first = Evaluate(binary.Left);
                if (first.AsNumber() is { } decided && Operators.ShortCircuits(op.Kind, decided))
                    return Value.Of(decided != 0);
                var second = Evaluate(binary.Right);

                // A `one` parameter and a repetition over words compare as words: the side
                // that is not already one is the bare name written beside it, which is a word
                // rather than a name and is never looked up.
                if (op.Kind is SyntaxKind.EqualsEquals or SyntaxKind.BangEquals
                    && (first.IsWord || second.IsWord)
                    && WordOf(first, binary.Left) is { } left && WordOf(second, binary.Right) is { } right)
                {
                    var same = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                    return Value.Of(op.Kind == SyntaxKind.EqualsEquals ? same : !same);
                }
                return Binary(op, first, second);

            case CallExpressionSyntax call:
                return Call(call);

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
    private Value ValueOfName(NameExpressionSyntax name)
    {
        if (BoundItem(name) is { } item)
            return Indexed(name, Evaluate(item));
        if (SymbolOf(name) is not { } symbol)
        {
            // Binding left a name in a value `.select` chooses between to whichever evaluation
            // chooses it.
            if (choosing > 0 && name.ChildTokens is [{ Kind: SyntaxKind.Identifier or SyntaxKind.CheapLocal } alone])
                Report(alone, $"`{alone.Text}` is not declared");
            return Value.Unknown;
        }
        if (arguments.TryGetValue(symbol, out var argument))
            return Indexed(name, argument);
        return Indexed(name, symbol.Kind == SymbolKind.Member ? OffsetAlong(name) : ValueOfSymbol(symbol));
    }

    /// <summary>
    /// <paramref name="value"/> with the elements any <c>[i]</c> along the path steps over:
    /// on a member's offset when the path runs through a type, and on an address when it
    /// starts at data. Every name a path can end in comes through here, so an index on one
    /// that reaches no declaration is refused rather than quietly dropped.
    /// </summary>
    private Value Indexed(NameExpressionSyntax name, Value value)
    {
        if (name.Indexes.Length == 0)
            return value;
        return IndexOffset(name) is { } stepped && value.AsNumber() is { } at
            ? Value.Of(at + stepped)
            : Value.Unknown;
    }

    /// <summary>
    /// How many bytes the indexes along a path come to, or null when one of them is wrong,
    /// which is reported here. An index is worked out before the program runs, so it is a
    /// constant and has to be an element the declaration holds.
    /// </summary>
    private long? IndexOffset(NameExpressionSyntax name)
    {
        long offset = 0;
        foreach (var (part, index) in ElementIndexes.Of(name))
        {
            if (!resolved.TryGetValue((name.Tree, part.Span.Start), out var symbol))
                return null;
            if (symbol.Kind is not (SymbolKind.Data or SymbolKind.Member) || symbol is { Kind: SymbolKind.Data, Data: null })
            {
                Report(index, symbol is { Kind: SymbolKind.Data, Data: null }
                    ? $"`{symbol.DisplayName}` is mixed data, which has bytes and no elements"
                    : $"`{symbol.DisplayName}` is {symbol.KindPhrase}, and `[i]` reaches an element of data");
                return null;
            }

            // A count nt65 cannot work out has already been reported where it is written.
            EvaluateSymbol(symbol);
            if (symbol.Count is not { } count || ElementIndexes.Stride(symbol) is not { } stride)
                return null;

            // An index the brackets hold nothing between stands in its slot with no text of its
            // own, and what is missing has already been said where the brackets are.
            var written = index.Index;
            if (written.Span.Length == 0)
                return null;
            if (Evaluate(written).AsNumber() is not { } at)
            {
                // A name in it that means nothing has been reported where it is written, and
                // the index is no constant only because of it.
                if (Names(written))
                {
                    Report(written, "an element index is a constant: an index worked out as the program runs "
                        + $"is what `{symbol.DisplayName},x` is for");
                }
                return null;
            }
            if (at < 0 || at >= count)
            {
                Report(written, at < 0
                    ? $"an element index is never negative, and this one is {at}"
                    : $"`{symbol.DisplayName}` holds {count} {(count == 1 ? "element" : "elements")}, "
                        + $"and the last of them is {count - 1}");
                return null;
            }
            offset += at * stride;
        }
        return offset;
    }

    /// <summary>
    /// The offset a path of members comes to. A type contributes nothing and a member its
    /// own offset; a path that starts at an instance is an address, which only the linker
    /// knows, so it has no value here and is written symbolically instead.
    /// </summary>
    private Value OffsetAlong(NameExpressionSyntax name)
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

    /// <summary>Whether every name an expression writes names something, declared or bound.</summary>
    private bool Names(SyntaxNode node) =>
        (node is not NameExpressionSyntax name || SymbolOf(name) is not null || BoundItem(name) is not null)
        && node.ChildNodes.All(Names);

    /// <summary>The address a path of members starts from, such as the instance of <c>pos::y</c>, or null.</summary>
    private Symbol? AddressAlong(NameExpressionSyntax name)
    {
        foreach (var token in name.ChildTokens)
        {
            if (resolved.TryGetValue((name.Tree, token.Span.Start), out var part) && part.IsAddress)
            {
                EvaluateSymbol(part);
                return part;
            }
        }
        return null;
    }

    /// <summary>
    /// An enum member with no value of its own: the one before it plus one, from zero, and
    /// nothing when the one before has no value.
    /// </summary>
    private long? Follows(Symbol member)
    {
        if (member.PreviousMember is not { } previous)
            return 0;
        EvaluateSymbol(previous);
        return previous.Value.AsNumber() is { } before ? before + 1 : null;
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
    private static string? WordOf(Value value, ExpressionSyntax written) => value.IsWord
        ? value.Text
        : written is NameExpressionSyntax { ChildTokens: [var word] }
            ? word.Text
            : null;

    private Value Binary(SyntaxToken op, Value left, Value right)
    {
        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
            return Reject(op, left.IsString ? left : right);
        if (b == 0 && Operators.Divides(op))
        {
            Report(op, "division by zero");
            return Value.Unknown;
        }
        return Operators.Binary(op, a, b) is { } result ? Value.Of(result) : Value.Unknown;
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
    private Value Call(CallExpressionSyntax call)
    {
        var given = call.Arguments.Arguments;
        if (call.Callee is { } callee)
            return Applied(callee, given);
        if (call.Function is not { Kind: SyntaxKind.Directive } function)
            return Value.Unknown;

        var name = function.Text.ToLowerInvariant();
        var arguments = given;

        if (name == ".select")
            return Select(function, arguments);

        // What a macro body adds asks about an argument rather than about a value, so each
        // reads the binding rather than evaluating what is written.
        if (name is ".mode" or ".empty")
        {
            if (arguments.Count != 1 || Argument(arguments[0]) is not { } about)
                return Value.Unknown;
            return name == ".mode"
                ? about.Operand is { } operand ? Value.Word(Operands.ModeOf(operand)) : Value.Unknown
                : Value.Of(about.Block is null || Macros.LinesOf(about.Block).Count == 0);
        }

        // `.endof` and `.spanof` describe layout rather than shape: they are address
        // expressions like any label difference. Only the difference can ever be a number,
        // because nt65 never knows an absolute address, and only a caller that has laid the
        // file out can supply it.
        if (name is ".endof" or ".spanof")
        {
            if (arguments.Count != 1 || SymbolOf(arguments[0]) is not { } laid)
                return Value.Unknown;
            if (NotAnExtent(laid, name, arguments[0]))
                return Value.Unknown;
            if (!HasBytesOfItsOwn(laid))
            {
                Report(arguments[0], $"`{laid.Name}` is {laid.KindPhrase} and takes no bytes of its own, "
                    + $"so `{name}` has nothing to measure");
                return Value.Unknown;
            }
            return name == ".spanof" && spans?.Invoke(laid) is { } span ? Value.Of(span) : Value.Unknown;
        }

        if (name is ".sizeof" or ".countof")
        {
            if (arguments.Count != 1 || SymbolOf(arguments[0]) is not { } measured)
                return Value.Unknown;

            // `.countof(p)` of a `list` parameter is how many arguments the call gave it.
            if (name == ".countof" && Argument(arguments[0]) is { Parameter.Kind: ParameterKind.List } listed)
                return Value.Of(listed.Items.Count);
            if (NotAnExtent(measured, name, arguments[0]))
                return Value.Unknown;

            // An enum counts its members.
            if (name == ".countof" && measured.Kind == SymbolKind.Enum)
                return Value.Of(measured.Body?.Symbols.Count(member => member.Kind == SymbolKind.Constant) ?? 0);

            // A routine and mixed data are bytes, not elements, and how many bytes a routine
            // takes is layout, which only a caller that has laid the file out knows.
            var bytesOnly = measured.Kind == SymbolKind.Proc || measured is { Kind: SymbolKind.Data, Data: null };
            if (name == ".countof" && bytesOnly)
            {
                Report(arguments[0], $"`{measured.Name}` is {(measured.Kind == SymbolKind.Proc ? "a routine" : "mixed data")}, "
                    + $"which has bytes and no elements: `.sizeof({measured.Name})` is how many bytes it takes");
                return Value.Unknown;
            }
            if (measured.Kind == SymbolKind.Proc)
                return spans?.Invoke(measured) is { } body ? Value.Of(body) : Value.Unknown;

            EvaluateSymbol(measured);
            var room = name == ".sizeof" ? measured.Size : measured.Count;
            if (room is null && measured is { Kind: SymbolKind.Data, Data: null })
            {
                Report(arguments[0], $"nt65 cannot say how many bytes `{measured.Name}` takes: an `.align` in it "
                    + $"depends on where it lands, and `.spanof({measured.Name})` measures it in the output");
            }
            return room is { } number ? Value.Of(number) : Value.Unknown;
        }

        // `.addrsize` asks about the shape of its argument rather than its value.
        if (name == ".addrsize")
        {
            return arguments.Count == 1 && SizeOf(arguments[0], null) is { } size
                ? Value.Of((long)size)
                : Value.Unknown;
        }

        // A condition in an expansion asks about the CPU as the build's conditions do.
        if (configuration is not null
            && Configuration.AboutTheCpu(name, function, arguments, configuration.Cpu,
                (_, message) => Report(function, message)) is { } answer)
        {
            return answer;
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

            // `.target`, `.has`, `.defined` and the three a macro body adds are answered before
            // this point, each by the pass that knows what they ask about.
            _ => Value.Unknown,
        };
    }

    /// <summary>
    /// <c>.select(c, a, b)</c>: <c>a</c> when the constant <c>c</c> holds, and <c>b</c> when it
    /// does not. Only the chosen value is evaluated.
    /// </summary>
    private Value Select(SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        if (arguments.Count != 3)
        {
            Report(function, "`.select` takes a condition and the two values it chooses between: `.select(c, a, b)`");
            return Value.Unknown;
        }
        var condition = Evaluate(arguments[0]);
        if (condition.AsNumber() is not { } holds)
        {
            // A function's body is read once with nothing given, when its parameters decide
            // nothing yet; both values are read then, for the cycles either might close.
            if (readingBody)
            {
                Evaluate(arguments[1]);
                Evaluate(arguments[2]);
            }
            else if (condition.IsString)
            {
                Report(arguments[0], "a `.select` condition is a number, and this is text");
            }
            else
            {
                Report(arguments[0], "a `.select` condition is a constant, and this is not one");
            }
            return Value.Unknown;
        }
        choosing++;
        var chosen = Evaluate(arguments[holds != 0 ? 1 : 2]);
        choosing--;
        return chosen;
    }

    /// <summary>
    /// The value a <c>.select</c> call chooses, or null when <paramref name="node"/> is no
    /// <c>.select</c> or its condition is no constant.
    /// </summary>
    private SyntaxNode? ChosenBy(SyntaxNode node) =>
        SelectArguments(node) is [var condition, var ifHolds, var otherwise]
            && Evaluate(condition).AsNumber() is { } holds
            ? holds != 0 ? ifHolds : otherwise
            : null;

    /// <summary>
    /// Whether a symbol has bytes of its own in the output, which is what an end and a span
    /// are the end and the span of.
    /// </summary>
    private static bool HasBytesOfItsOwn(Symbol symbol) => symbol.Kind is SymbolKind.Proc or SymbolKind.Data;

    /// <summary>
    /// A label and a scope are the two names that look as if they had an extent and have none:
    /// a label is only a position, and a scope only a namespace. Says so, and whether it did.
    /// </summary>
    private bool NotAnExtent(Symbol symbol, string function, SyntaxNode at)
    {
        if (at is NameExpressionSyntax { Indexes.Length: > 0 })
        {
            Report(at, $"`{function}` measures a declaration, and `{at.GetText().Trim()}` is a place in one");
            return true;
        }
        var what = symbol.Kind switch
        {
            SymbolKind.Label => "a label, which is only a position",
            SymbolKind.Scope => "a scope, which is only a namespace",
            _ => null,
        };
        if (what is null)
            return false;
        Report(at, $"`{symbol.DisplayName}` is {what}: `{function}` measures a `.data` declaration, a routine or a type");
        return true;
    }

    /// <summary>What a name's macro parameter was given, or null when it names no parameter.</summary>
    private MacroArgument? Argument(SyntaxNode name) =>
        SymbolOf(name) is { } symbol ? given.GetValueOrDefault(symbol) : null;

    /// <summary>
    /// A charmap or a function called by name. A charmap maps one character to its byte; a
    /// function evaluates its body with each parameter bound to the argument it was given,
    /// and a function that ends up needing itself is the same cycle any constant would be.
    /// </summary>
    private Value Applied(NameExpressionSyntax callee, IReadOnlyList<SyntaxNode> given)
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
        foreach (var line in charmap.Entries)
        {
            if (line is not CharmapEntrySyntax entry)
                continue;
            var first = Evaluate(entry.First).AsNumber();
            var last = entry.Last is { } end ? Evaluate(end).AsNumber() : first;
            var to = Evaluate(entry.Value).AsNumber();
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
    private AddressSize? SizeOf(SyntaxNode expression, string? segment, Value? known = null)
    {
        AddressSize? widest = null;
        var named = false;
        Walk(expression);
        return named ? widest : (known ?? Evaluate(expression)).ImpliedAddressSize();

        void Walk(SyntaxNode node)
        {
            // A span is the difference of two addresses, which is a number; an end is an
            // address, as wide as the label it follows.
            if (node is CallExpressionSyntax { Function: { } function }
                && function.Text.Equals(".spanof", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (node is CurrentAddressExpressionSyntax)
            {
                named = true;
                widest = Widest(widest, SegmentSize(segment));
                return;
            }
            if (SelectArguments(node) is not null)
            {
                if (ChosenBy(node) is { } chosen)
                    Walk(chosen);
                return;
            }
            if (node is NameExpressionSyntax name)
            {
                // A member reached through an instance, `pos::y`, is a place in the instance,
                // and as wide an address as the instance is.
                if ((SymbolOf(name) is { IsAddress: true } symbol ? symbol : AddressAlong(name)) is { } address)
                {
                    named = true;
                    widest = Widest(widest, address.AddressSizeIn(name.Tree));
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
        if (node is CurrentAddressExpressionSyntax)
            return true;
        if (node is NameExpressionSyntax name)
            return SymbolOf(name) is { IsAddress: true };
        if (SelectArguments(node) is not null)
            return ChosenBy(node) is { } chosen && NamesAnAddress(chosen);
        foreach (var child in node.ChildNodes)
        {
            if (NamesAnAddress(child))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The arguments of a <c>.select</c> call, or null when <paramref name="node"/> is not one.
    /// </summary>
    internal static IReadOnlyList<SyntaxNode>? SelectArguments(SyntaxNode node) =>
        node is CallExpressionSyntax { Function: { Kind: SyntaxKind.Directive } function } call
            && function.Text.Equals(".select", StringComparison.OrdinalIgnoreCase)
            ? call.Arguments.Arguments
            : null;

    /// <summary>
    /// How much room a data directive takes: the bytes it generates, and how many elements
    /// they are. Null where nt65 cannot say — an `.align`, whose size depends on where it
    /// lands, or a directive whose operands do not add up.
    /// </summary>
    private DataSize? RoomFor(StatementSyntax written)
    {
        // A line of a body is its values, each one element of the type the body is of.
        if (written is DataValuesSyntax values)
        {
            return DataSyntax.DirectiveOfValues(values) is { } of && ElementWidth(of) is { } each
                ? Spread(values.Values, each)
                : null;
        }
        if (written is not DataDirectiveSyntax directive)
            return null;
        if (DataSyntax.IsElementType(directive))
            return RoomForElements(directive);
        var operands = directive.Values;

        switch (DataSyntax.NameOf(directive))
        {
            case ".lobytes":
            case ".hibytes":
            case ".bankbytes":
                return Spread(operands, width: 1);

            // `.strz` is the text and the zero byte that ends it.
            case ".strz":
                return Spread(operands, width: 1) is { } text
                    ? new DataSize(text.Bytes + 1, text.Elements + 1)
                    : null;

            case ".res":
                return operands.Length > 0 && Evaluate(operands[0]).AsNumber() is { } reserved and >= 0
                    ? new DataSize(reserved, reserved)
                    : null;

            case ".incbin":
                return RoomForBinary(directive, operands);

            // An `.align` generates however many bytes it takes to reach the next boundary,
            // which depends on where it lands, so it has no size of its own.
            default:
                return null;
        }
    }

    /// <summary>
    /// An element type: as many elements as its count says, or as its values come to, or one
    /// when it has neither, each as big as the element type — a record's being its type's size,
    /// which is also the stride of an array of them.
    /// </summary>
    private DataSize? RoomForElements(DataDirectiveSyntax directive)
    {
        if (ElementWidth(directive) is not { } width)
            return null;
        var count = directive.Count is { } counted
            ? counted.Count is null ? GivenCount(directive) ?? 0 : DeclaredCount(directive)
            : GivenCount(directive) ?? 1;
        return count is { } many and >= 0 ? new DataSize(width * many, many) : null;
    }

    /// <summary>How many bytes one element of an element type takes: a record's is its type's size.</summary>
    private long? ElementWidth(DataDirectiveSyntax directive)
    {
        if (directive.Type is not { } named)
            return SyntaxFacts.ElementSize(DataSyntax.NameOf(directive));
        if (SymbolOf(named) is not { } type)
            return null;
        EvaluateSymbol(type);
        return type.IsLayout ? type.Size : null;
    }

    /// <summary>The <c>n</c> of <c>[n]</c>, or null when there is none or it is no constant.</summary>
    private long? DeclaredCount(DataDirectiveSyntax directive) =>
        directive.Count?.Count is { } count ? Evaluate(count).AsNumber() : null;

    /// <summary>
    /// How many elements an element type's values come to, wherever they are written: after it
    /// on the line, in braces, or in the body its line opens. Null when it has none, or when
    /// nt65 cannot count them.
    /// </summary>
    private long? GivenCount(DataDirectiveSyntax directive)
    {
        if (DataSyntax.BracedOf(directive) is { } braced)
            return braced is ValueListSyntax list ? Spread(list.Values, 1).Elements : 1;
        if (DataSyntax.BodyOf(directive) is { } body)
        {
            return body.BlockKind == BlockKind.RecordInitializer
                ? 1
                : Total(body.Members, 1, statement =>
                    statement is DataValuesSyntax row ? Spread(row.Values, 1).Elements : 0, _ => null);
        }
        var values = DataSyntax.ValuesOf(directive);
        return values.Count > 0 ? Spread(values, 1).Elements : null;
    }

    /// <summary>
    /// How many bytes mixed data takes: its lines, and the data declared in it, with its
    /// conditionals decided and its repetitions unrolled. Null when an `.align` in it makes
    /// that depend on where it lands.
    /// </summary>
    private long? RoomForMixed(BlockSyntax block) => Total(block.Members, 1, BytesOnLine, nested =>
        nested.BlockKind switch
        {
            BlockKind.Data => RoomForMixed(nested),
            BlockKind.DataBody or BlockKind.RecordInitializer => BytesOnLine(nested.Opener.Statement),
            _ => null,
        });

    /// <summary>The bytes one line of mixed data takes, or null when nt65 cannot say.</summary>
    private long? BytesOnLine(StatementSyntax statement)
    {
        var directive = statement switch
        {
            DataDirectiveSyntax data => data,
            DataDeclarationSyntax declaration => declaration.Directive,
            LabeledLineSyntax labeled => labeled.Statement as DataDirectiveSyntax,
            _ => null,
        };
        return directive is null ? 0 : RoomFor(directive)?.Bytes;
    }

    /// <summary>
    /// What the lines of a body come to, one number per line from <paramref name="line"/> and
    /// per block from <paramref name="block"/>, with the conditionals decided as the build
    /// decides them and the repetitions unrolled a turn at a time. Null as soon as any part of
    /// it is unknown.
    /// </summary>
    private long? Total(
        IReadOnlyList<SyntaxNode> children, int from, Func<StatementSyntax, long?> line, Func<BlockSyntax, long?> block)
    {
        long total = 0;
        var chaining = false;
        var taken = false;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            long? part;
            if (child is not BlockSyntax nested)
            {
                chaining = false;
                part = child is LineSyntax written ? line(written.Statement) : 0;
            }
            else
            {
                var opener = nested.Opener.Statement;
                switch (nested.BlockKind)
                {
                    case BlockKind.If:
                        var continues = opener is ElseIfDirectiveSyntax or ElseDirectiveSyntax;
                        var take = (!continues || chaining) && Holds(nested, opener, continues && taken);
                        chaining = true;
                        taken = (continues && taken) || take;
                        part = take ? Total(nested.Members, 1, line, block) : 0;
                        break;
                    case BlockKind.Repeat or BlockKind.Each when opener is RepetitionDirectiveSyntax repetition:
                        chaining = false;
                        part = Turns(nested, repetition, () => Total(nested.Members, 1, line, block));
                        break;
                    default:
                        chaining = false;
                        part = block(nested);
                        break;
                }
            }
            if (part is not { } known)
                return null;
            total += known;
        }
        return total;
    }

    /// <summary>
    /// Whether a branch of a conditional is taken: as the build answered it where it could, and
    /// otherwise by its condition, as one inside an expansion is.
    /// </summary>
    private bool Holds(BlockSyntax block, StatementSyntax opener, bool already)
    {
        if (configuration?.Answered(block) == true)
            return configuration.Includes(block);
        if (already)
            return false;
        if (opener is ElseDirectiveSyntax)
            return true;
        return opener is ConditionalDirectiveSyntax conditional && Evaluate(conditional.Condition).AsNumber() is { } value and not 0;
    }

    /// <summary>
    /// The sum of <paramref name="body"/> over every turn of a repetition, with the name it
    /// binds standing for that turn's index, item or member. Null when the turns are unknown.
    /// </summary>
    private long? Turns(BlockSyntax block, RepetitionDirectiveSyntax opener, Func<long?> body)
    {
        var counted = opener.Expression;
        var binding = BindingIn(block, opener);
        List<Action> turns = [];
        if (opener is RepeatDirectiveSyntax)
        {
            if (Evaluate(counted).AsNumber() is not { } count || count < 0)
                return null;
            if (binding is null)
                return body() * count;
            for (long i = 0; i < count; i++)
            {
                var turn = i;
                turns.Add(() => arguments[binding] = Value.Of(turn));
            }
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.List } list)
        {
            turns.AddRange(list.Items.Select(item => (Action)(() => { if (binding is not null) items[binding] = item; })));
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.Enum, Body: { } walked })
        {
            turns.AddRange(walked.Symbols.Select(member => (Action)(() =>
            {
                if (binding is null)
                    return;
                arguments[binding] = member.Value;
                members[binding] = member;
            })));
        }
        else
        {
            return null;
        }

        long total = 0;
        foreach (var turn in turns)
        {
            turn();
            if (body() is not { } part)
                return null;
            total += part;
        }
        if (binding is not null)
        {
            arguments.Remove(binding);
            items.Remove(binding);
            members.Remove(binding);
        }
        return total;
    }

    /// <summary>The name a repetition binds, found where its body names it; null when nothing does.</summary>
    private Symbol? BindingIn(BlockSyntax block, RepetitionDirectiveSyntax opener)
    {
        if (opener.Name is not { } declared)
            return null;
        foreach (var name in block.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            foreach (var token in name.ChildTokens)
            {
                if (resolved.TryGetValue((name.Tree, token.Span.Start), out var symbol)
                    && symbol.Kind == SymbolKind.Binding && symbol.Tree == block.Tree
                    && symbol.NameSpan.Start == declared.Span.Start)
                {
                    return symbol;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// One element per operand, except that text is one element per byte and a list stands
    /// for its own items.
    /// </summary>
    private DataSize Spread(IReadOnlyList<SyntaxNode> operands, long width)
    {
        long elements = 0;
        foreach (var operand in operands)
        {
            if (BytesIn(operand) is { } bytes)
                elements += bytes.Count;
            else if (operand is NameExpressionSyntax name && SymbolOf(name) is { Kind: SymbolKind.List } list)
                elements += list.Items.Count;
            else
                elements++;
        }
        return new DataSize(elements * width, elements);
    }

    /// <summary>
    /// A binary file, whose length nt65 reads for itself. The path is relative to the file
    /// that names it, and an offset and a length may narrow it.
    /// </summary>
    private DataSize? RoomForBinary(DataDirectiveSyntax directive, IReadOnlyList<SyntaxNode> operands)
    {
        if (operands.Count == 0 || Evaluate(operands[0]) is not { Kind: ValueKind.String, Text: { } path })
            return null;

        var from = Paths.Beside(directive.Tree.Path, path);
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
        // A macro parameter given text is that text, as many bytes as it has.
        if (operand is NameExpressionSyntax bound && BoundItem(bound) is { } item)
            return BytesIn(item);

        // A string constant is its text wherever it is named, as a literal would be.
        if (operand is NameExpressionSyntax constant && SymbolOf(constant) is { Kind: SymbolKind.Constant }
            && Evaluate(constant) is { Kind: ValueKind.String, Text: { } named })
            return [.. named.Select(c => (long)c)];

        if (operand is StringExpressionSyntax or CharacterExpressionSyntax)
        {
            var value = Evaluate(operand);
            return value.Kind switch
            {
                ValueKind.String => [.. value.Text!.Select(c => (long)c)],
                ValueKind.Number => [value.Number],
                _ => null,
            };
        }

        if (operand is not CallExpressionSyntax { Callee: { } callee } call
            || SymbolOf(callee) is not { Kind: SymbolKind.Charmap } charmap)
        {
            return null;
        }

        var given = call.Arguments.Arguments;
        if (given.Count != 1)
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
            member.Type ??= (member.Data as DataDirectiveSyntax)?.Type is { } named ? SymbolOf(named) : null;

            // A member reserves room and holds no value, so an operand would be silently
            // ignored, and `colors: .word 16` read as sixteen words would be two bytes.
            var room = member.Data is DataDirectiveSyntax data
                && (DataSyntax.IsElementType(data) || DataSyntax.NameOf(data) == ".res")
                ? RoomFor(data)
                : null;
            if (member.Data is DataDirectiveSyntax element && DataSyntax.IsElementType(element))
            {
                var spelled = element.Directive.Text;
                if ((DataSyntax.ValuesOf(element).FirstOrDefault() ?? DataSyntax.BracedOf(element)) is { } valued)
                {
                    Report(valued, $"`{member.Name}` is a member, which reserves room and holds no value: "
                        + $"several are `{spelled}[n]`");
                }
                else if (element.Count is { Count: null } count)
                {
                    Report(count, $"`{member.Name}` is a member, whose count is a number: `{spelled}[n]`");
                }
            }
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
    private Symbol? SymbolOf(NameExpressionSyntax name)
    {
        // A name bound to a list item is that item: `.each handlers, h` makes `h` the label
        // it stands for, with that label's address size and everything else about it.
        if (BoundItem(name) is NameExpressionSyntax item)
            return SymbolOf(item);

        var tokens = name.ChildTokens;
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            if (resolved.TryGetValue((name.Tree, tokens[i].Span.Start), out var symbol))
                return i > 0 && symbol.Kind == SymbolKind.Binding ? Namesake(name, i, symbol) : symbol;
        }
        return null;
    }

    /// <summary>The same, for whatever is written where a name may be: only a name stands for a symbol.</summary>
    private Symbol? SymbolOf(SyntaxNode written) => written is NameExpressionSyntax name ? SymbolOf(name) : null;

    /// <summary>
    /// What <c>actions::c</c> names, where <c>c</c> walks an enum: the member of <c>actions</c>
    /// with the same name as the enum member <c>c</c> stands for on this turn. Off any turn,
    /// as an editor asks, it names nothing.
    /// </summary>
    private Symbol? Namesake(NameExpressionSyntax name, int last, Symbol binding)
    {
        Symbol? container = null;
        var tokens = name.ChildTokens;
        for (var i = last - 1; i >= 0 && container is null; i--)
            resolved.TryGetValue((name.Tree, tokens[i].Span.Start), out container);
        if (container?.Body is not { } body)
            return null;

        if (!members.TryGetValue(binding, out var member))
        {
            if (items.ContainsKey(binding) || arguments.ContainsKey(binding))
            {
                Report(name, $"`{binding.Name}` does not walk an enum, so it names no member: a path "
                    + "ends in a repetition's name only over an enum's members");
            }
            return null;
        }
        if (body.FindMember(member.Name) is { } namesake)
            return namesake;
        Report(name, $"`{container.Name}` has no `{member.Name}`, which `{binding.Name}` stands for on this turn");
        return null;
    }

    /// <summary>The item a written name is bound to on this turn, or null when it is bound to none.</summary>
    private SyntaxNode? BoundItem(NameExpressionSyntax name)
    {
        if (items.Count == 0 || name.ChildTokens is not [var only])
            return null;
        return resolved.TryGetValue((name.Tree, only.Span.Start), out var symbol)
            && items.TryGetValue(symbol, out var item)
            ? item
            : null;
    }

    private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

    private void Report(Span span, string message, IReadOnlyList<RelatedSpan> related) =>
        Add(new Diagnostic(span, Severity.Error, message, related));

    private void Report(SyntaxToken token, string message) =>
        Add(new Diagnostic(token.Parent.Tree.GetSpan(token.Span), Severity.Error, message, []));

    private void Report(SyntaxNode node, string message) =>
        Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message, []));

    private void Add(Diagnostic diagnostic)
    {
        if (diagnostics is null)
            return;
        diagnostics.Add(diagnostic);
        owners?.Add(owner?.Tree.Path ?? diagnostic.Span.File);
    }
}
