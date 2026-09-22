using System.Collections.ObjectModel;
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
internal sealed partial class Evaluator
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

    // What one pass over a span of code costs, which only layout knows and only a caller that
    // has laid the file out can answer.
    private readonly Func<Symbol, Symbol, bool, CycleSpan>? cycles;

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

    // Set when what is being evaluated is a build's own condition, which is read before there
    // are any declarations and so means something narrower by every name and every call.
    private readonly Conditions? conditions;

    // The declarations already told that a value of theirs is wider than ca65 can hold. The
    // steps around such a value are the same mistake, and the first of them is the one to name.
    private readonly HashSet<Symbol> wide = [];

    // The literals already told that they hold a character no encoding is settled for. A macro
    // body is evaluated once per call, and its text is one piece of text however often it is.
    private readonly HashSet<SyntaxNode> outsideAscii = [];

    // The data declarations whose places are being worked out, so that one asked about by
    // what is written inside it answers unknown rather than asking itself again.
    private readonly HashSet<Symbol> placing = [];
    private Symbol? owner;

    // The symbol whose own value is being worked out, whose every step the output carries.
    private Symbol? declaring;

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
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null,
        Func<Symbol, bool>? settled = null,
        List<string>? owners = null,
        Configuration? configuration = null,
        Conditions? conditions = null)
    {
        this.conditions = conditions;
        this.configuration = configuration;
        this.settled = settled;
        this.owners = owners;
        this.segments = segments;
        this.resolved = resolved;
        this.diagnostics = diagnostics;
        this.binaryLength = binaryLength;
        this.spans = spans;
        this.cycles = cycles;

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
        Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        new Evaluator(segments, resolved, diagnostics, binaryLength, bound, spans, cycles).Bytes(expression);

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
                && SymbolOf(name) is { Kind: SymbolKind.Scope } scope && name.LastPart?.Name.Text == scope.Name)
                Report(name, Catalogue.ScopeHasNoAddress.Says(scope.Name));
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

    /// <summary>The symbol a written name stands for, or null when it names none.</summary>
    public static Symbol? SymbolNamed(
        SyntaxNode name,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        name is NameExpressionSyntax written
            ? new Evaluator(SegmentTable.Standard, resolved, null, null, bound).SymbolOf(written)
            : null;

    /// <summary>
    /// For <c>.exprof(p)</c>, the expression inside the operand the call passed as <c>p</c>, with
    /// <paramref name="bound"/> saying what each parameter was passed; null when <c>p</c> is not an
    /// <c>operand</c> parameter.
    /// </summary>
    public static SyntaxNode? ExprOf(
        CallExpressionSyntax call,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound) =>
        new Evaluator(SegmentTable.Standard, resolved, null, null, bound).ExprOf(call);

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
        Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        new Evaluator(segments, resolved, null, null, bound, spans, cycles).Evaluate(expression);

    /// <summary>
    /// An evaluator for the conditions of a build, which are answered before a declaration
    /// exists. Nothing resolves in one, so it carries no symbols at all: what a name and a
    /// call may mean there is <paramref name="conditions"/>.
    /// </summary>
    public static Evaluator ForConditions(Conditions conditions, List<Diagnostic> diagnostics) =>
        new(SegmentTable.Standard, ReadOnlyDictionary<(SyntaxTree, int), Symbol>.Empty, diagnostics,
            conditions: conditions);

    /// <summary>
    /// What an expression is worth. A caller that reports — the pass over the symbols, and the
    /// conditions of a build — evaluates through here; one that only asks goes through
    /// <see cref="ValueOf"/>.
    /// </summary>
    public Value Evaluate(SyntaxNode node) => declaring is null ? Evaluated(node) : Carried(node, Evaluated(node));

    /// <summary>
    /// A value on its way into the output, checked against what ca65 can hold. ca65 computes
    /// in 32 bits and the output writes a declaration's expression as the source wrote it, so
    /// every step of one has to be a number ca65 reaches too.
    /// </summary>
    private Value Carried(SyntaxNode node, Value value)
    {
        // A name is worth what its own declaration says, and what does not fit there is said
        // there rather than again at every name of it.
        if (node is NameExpressionSyntax || declaring is not { } within)
            return value;
        if (value.AsNumber() is { } number && !FitsCa65(number) && wide.Add(within))
            Report(node, TooWide(number));
        return value;
    }

    /// <summary>What is wrong with a value ca65 has no room for.</summary>
    private static DiagnosticMessage TooWide(long number) => Catalogue.NumberTooWide.Says(Value.Of(number));

    /// <summary>
    /// Outside a charmap, text is ASCII and <c>\xHH</c> writes any byte, so a character typed
    /// directly above <c>$7f</c> is an error rather than a byte of some encoding. A charmap
    /// entry is where such a character says what it becomes, and what a data declaration holds
    /// is checked where its bytes are laid out, which is where the charmap applied to it is
    /// known; everything else — a constant, an operand, a condition — is said here.
    /// </summary>
    private void CheckAscii(LiteralExpressionSyntax literal)
    {
        if (!literal.Token.Text.Any(c => c > 127) || InCharmapOrData(literal) || !outsideAscii.Add(literal))
            return;
        Report(literal, Catalogue.TextNotAscii);
    }

    /// <summary>Whether the literal stands somewhere a charmap answers for it, or somewhere layout checks it.</summary>
    private bool InCharmapOrData(SyntaxNode literal)
    {
        for (var node = literal.Parent; node is not null; node = node.Parent)
        {
            if (node is CharmapEntrySyntax or DataDirectiveSyntax or DataValuesSyntax)
                return true;
            if (node is CallExpressionSyntax { Callee: { } callee } && SymbolOf(callee)?.Kind == SymbolKind.Charmap)
                return true;
        }
        return false;
    }

    /// <summary>Whether ca65 can hold a value: its own arithmetic is 32 bits, and it reads one unsigned.</summary>
    private static bool FitsCa65(long value) => value is >= -0x80000000L and <= 0xffffffffL;

    private Value Evaluated(SyntaxNode node)
    {
        // A literal the lexer refused is worth nothing, the way an undeclared name is: what is
        // wrong with it has been said once, where it is written, and reading it for a value
        // would be reading digits that are not digits.
        if (node is LiteralExpressionSyntax { Token.ContainsDiagnostics: true })
            return Value.Unknown;

        switch (node)
        {
            case NumberExpressionSyntax number:
                return Number(Literals.Number(number.Token.Text));

            case CharacterExpressionSyntax character:
                CheckAscii(character);
                return Number(Literals.Character(character.Token.Text));

            case StringExpressionSyntax quoted:
                CheckAscii(quoted);
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

                // Two places in one data declaration are a known distance apart wherever it lands.
                if (op.Kind == SyntaxKind.Minus && (first.Kind == ValueKind.Unknown || second.Kind == ValueKind.Unknown)
                    && Distance(binary) is { } distance)
                {
                    return Value.Of(distance);
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
    /// Works <paramref name="symbol"/> out unless it has been worked out already. Only the
    /// pass that reports may: by the time anything else asks, every symbol has been evaluated
    /// and every type laid out, and answering a question must never write to a symbol another
    /// thread is reading.
    /// </summary>
    private void Settle(Symbol symbol)
    {
        if (diagnostics is not null)
            EvaluateSymbol(symbol);
    }

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

        // The type a `.type T` names is worth keeping on the symbol: emission walks into it,
        // an editor asks what a path reaches through it, and nothing else would have resolved
        // it unless a path happened to lead that way.
        symbol.Type ??= symbol.TypeExpression is NameExpressionSyntax typed ? SymbolOf(typed) : null;

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
                if (symbol.Value.AsNumber() is { } counted && !FitsCa65(counted) && wide.Add(symbol))
                    Report(symbol.DeclarationSpan, TooWide(counted), []);
                return;
            default:
                break;
        }

        // A data declaration takes its size and its element count from what it declares, which
        // is what `.sizeof` and `.countof` answer for it. Mixed data has bytes and no elements.
        // An import that writes an element type is sized from it in exactly the same way: what
        // the import says is what nt65 works with, as a routine import's signature is.
        if (symbol.Kind == SymbolKind.Data || symbol.IsTypedStorage)
        {
            // How much room a declaration takes is nt65's own arithmetic, whatever is being
            // declared around it: what the output carries of it is the count of a `.res`.
            var outerSizing = declaring;
            declaring = null;
            evaluating.Add(symbol);
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
            declaring = outerSizing;
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

        // What the symbol is worth is what the output carries, and so is every step of the
        // expression it is written with: ca65 works those steps out again from the text.
        evaluating.Add(symbol);
        var outerDeclaring = declaring;
        declaring = symbol;
        symbol.Value = Evaluate(expression);
        declaring = outerDeclaring;
        evaluating.RemoveAt(evaluating.Count - 1);

        // `NAME = expr` is a constant if the expression names no address, and an address
        // alias if it does. Imports and extern procs are already classified.
        // A distance between two places in one data declaration names addresses and is a
        // constant all the same, because nt65 has its value.
        if (symbol.Kind == SymbolKind.Constant && symbol.Value.AsNumber() is null && NamesAnAddress(expression))
        {
            // An enum is a set of numbers, and one that stood for an address would give every
            // member after it a value nothing can work out.
            if (symbol.IsEnumMember)
            {
                Report(expression, Catalogue.EnumMemberIsNotAnAddress.Says(symbol.Name));
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

        // Every symbol on the ring is left without a value, and a type on one without a
        // layout: whoever walks into a type later has to be able to tell.
        foreach (var member in ring)
            member.IsCyclic = true;
        var symbol = ring[0];
        Report(symbol.DeclarationSpan, Catalogue.DefinedInTermsOfItself.Says(symbol.DisplayName),
            [.. ring.Skip(1).Select(other =>
                new RelatedSpan(other.DeclarationSpan, $"through `{other.DisplayName}`"))]);
    }

    /// <summary>
    /// What a written name is worth. Inside a function body a parameter stands for the
    /// argument it was called with; a member reached through a path is the offsets along
    /// that path added up, which is what makes `Player::pos::y` a number.
    /// </summary>
    private Value ValueOfName(NameExpressionSyntax name)
    {
        if (conditions is not null)
            return InCondition(name, conditions);
        if (BoundItem(name) is { } item)
            return Indexed(name, Evaluate(item));
        if (SymbolOf(name) is not { } symbol)
        {
            // Binding left a name in a value `.select` chooses between to whichever evaluation
            // chooses it.
            if (choosing > 0 && name.SimpleName is { Kind: SyntaxKind.Identifier or SyntaxKind.CheapLocal } alone)
                Report(alone, Catalogue.NotDeclared.Says(alone.Text, ""));
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
        if (!name.IsIndexed)
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
            if (!(symbol.IsTypedStorage || symbol.Kind == SymbolKind.Member)
                || symbol is { Kind: SymbolKind.Data, Data: null })
            {
                Report(index, Catalogue.NotIndexable.Says(
                    symbol.DisplayName,
                    symbol is { Kind: SymbolKind.Data, Data: null }
                        ? "mixed data, which has bytes and no elements"
                        : $"{symbol.KindPhrase}, and `[i]` reaches an element of data"));
                return null;
            }

            // A count nt65 cannot work out has already been reported where it is written.
            Settle(symbol);
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
                    Report(written, Catalogue.ElementIndexNotConstant.Says(symbol.DisplayName));
                }
                return null;
            }
            if (at < 0 || at >= count)
            {
                Report(written, Catalogue.ElementIndexOutOfRange.Says(at < 0
                    ? $"an element index is never negative, and this one is {at}"
                    : $"`{symbol.DisplayName}` holds {count} {(count == 1 ? "element" : "elements")}, "
                        + $"and the last of them is {count - 1}"));
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
        foreach (var token in name.Names)
        {
            if (!resolved.TryGetValue((name.Tree, token.Span.Start), out var part))
                continue;
            if (part.IsAddress)
                return Value.Unknown;
            if (part.Kind != SymbolKind.Member)
                continue;
            Settle(part);
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
        Settle(symbol);
        return symbol.Value;
    }

    private Value Unary(SyntaxToken op, Value operand)
    {
        if (operand.AsNumber() is not { } value)
            return Reject(op, operand);
        if (Operators.Unary(op.Kind, value, out var refused) is { } result)
            return Value.Of(result);
        if (refused is { } why)
            Report(op, why);
        return Value.Unknown;
    }

    /// <summary>
    /// The word one side of a comparison stands for: the value when it is already a word, or
    /// else the bare name written there, which a word is compared against unlooked-up.
    /// </summary>
    private static string? WordOf(Value value, ExpressionSyntax written) => value.IsWord
        ? value.Text
        : written is NameExpressionSyntax { SimpleName: { } word }
            ? word.Text
            : null;

    private Value Binary(SyntaxToken op, Value left, Value right)
    {
        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
            return Reject(op, left.IsString ? left : right);
        if (b == 0 && Operators.Divides(op))
        {
            Report(op, Catalogue.DivisionByZero);
            return Value.Unknown;
        }
        if (Operators.Binary(op, a, b, out var refused) is { } result)
            return Value.Of(result);
        if (refused is { } why)
            Report(op, why);
        return Value.Unknown;
    }

    /// <summary>An operand that is a string where a number belongs; there is no string arithmetic.</summary>
    private Value Reject(SyntaxToken op, Value operand)
    {
        if (operand.IsString)
            Report(op, Catalogue.OperatorOnText.Says(op.Text));
        return Value.Unknown;
    }

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

        // A name written from the top level is already a path, so its first part is no more the
        // whole of it than a second part would be.
        var reached = name.GlobalToken is not null;
        var names = name.Names;
        for (var i = names.Length - 1; i >= 0; i--)
        {
            if (resolved.TryGetValue((name.Tree, names[i].Span.Start), out var symbol))
                return (i > 0 || reached) && symbol.Kind == SymbolKind.Binding ? Namesake(name, i, symbol) : symbol;
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
        var names = name.Names;
        for (var i = last - 1; i >= 0 && container is null; i--)
            resolved.TryGetValue((name.Tree, names[i].Span.Start), out container);
        if (container?.Body is not { } body)
            return null;

        if (!members.TryGetValue(binding, out var member))
        {
            if (items.ContainsKey(binding) || arguments.ContainsKey(binding))
            {
                Report(name, Catalogue.BindingNotOverAnEnum.Says(binding.Name));
            }
            return null;
        }
        if (body.FindMember(member.Name) is { } namesake)
            return namesake;
        Report(name, Catalogue.FamilyMemberMissing.Says(container.Name, member.Name, binding.Name));
        return null;
    }

    /// <summary>The item a written name is bound to on this turn, or null when it is bound to none.</summary>
    private SyntaxNode? BoundItem(NameExpressionSyntax name)
    {
        if (items.Count == 0 || name.SimpleName is not { } only)
            return null;
        return resolved.TryGetValue((name.Tree, only.Span.Start), out var symbol)
            && items.TryGetValue(symbol, out var item)
            ? item
            : null;
    }

    private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

    private void Report(Span span, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related) =>
        Add(new Diagnostic(span, Severity.Error, message, related));

    private void Report(SyntaxToken token, DiagnosticMessage message) =>
        Add(new Diagnostic(token.Parent.Tree.GetSpan(token.Span), Severity.Error, message, []));

    private void Report(SyntaxNode node, DiagnosticMessage message) =>
        Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message, []));

    private void Add(Diagnostic diagnostic)
    {
        if (diagnostics is null)
            return;
        diagnostics.Add(diagnostic);
        owners?.Add(owner?.Tree.Path ?? diagnostic.Span.File);
    }
}
