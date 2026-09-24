using System.Collections.ObjectModel;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Computes the values of expressions, and from them each symbol's kind, value and address
/// size.
/// <para>
/// A constant may appear before the names it uses, so evaluation follows references rather than
/// the order of the file. A name whose value depends on itself is an error, reported once for
/// the whole cycle. Everything built only from constants gets a value. An expression that names
/// an address gets none, and is emitted symbolically for ca65 to resolve.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    private readonly EvaluationMode mode;
    private readonly SegmentTable segments;
    private readonly IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved;

    // What the names bound where evaluation started stand for. A repetition or a function call
    // binds more of them while evaluation is inside it.
    private readonly BoundNames names;
    private readonly Func<string, long?>? binaryLength;
    private readonly Func<Symbol, long?>? spans;
    private readonly Func<Symbol, Symbol, bool, CycleSpan>? cycles;
    private readonly Configuration? configuration;

    // Receives each diagnostic found, with the file of the symbol being evaluated when it was
    // found. A problem found while evaluating a symbol belongs to that symbol's file, even when
    // it is found in another file's function body, so a file that is not evaluated again keeps
    // reporting it. A query discards what it finds.
    private readonly Action<Diagnostic, string> report;

    // For a program in which one file changed, `unchanged` identifies the symbols of the files
    // that did not change, whose values are kept and not evaluated again. `unchangedReads`
    // collects the ones this file's symbols read.
    private readonly Func<Symbol, bool> unchanged;
    private readonly HashSet<Symbol> unchangedReads = [];

    // Decides what a name and a call may mean in a build's condition. Only an evaluator for
    // conditions has one.
    private readonly Conditions? conditions;
    private readonly HashSet<Symbol> evaluated = [];
    private readonly List<Symbol> evaluating = [];

    // The declarations already reported for a value wider than ca65 can hold. The steps of the
    // same expression around that value are the same mistake, so only the first is reported.
    private readonly HashSet<Symbol> wide = [];

    // How many problems evaluation has met, counting one it found again and did not report. A
    // call that meets one is evaluated again at each use, so that each use reports it.
    private int problems;

    // The literals already reported for holding a character outside ASCII. A macro body is
    // evaluated once per call, but each literal in it is one piece of source and is reported
    // once.
    private readonly HashSet<SyntaxNode> outsideAscii = [];

    // The data declarations being searched for a location inside them, so that a question
    // about one from code inside it gets an unknown answer rather than recursing.
    private readonly HashSet<Symbol> placing = [];

    // Where the walk over the program is. Every change to it is undone by the scope that made
    // it, however that scope ends.
    private WalkContext context;

    private Evaluator(
        EvaluationMode mode,
        EvaluationInputs inputs,
        Action<Diagnostic, string> report,
        Func<Symbol, bool>? unchanged = null,
        Conditions? conditions = null)
    {
        this.mode = mode;
        segments = inputs.Segments;
        names = inputs.Names;
        resolved = names.Resolved;
        binaryLength = inputs.BinaryLength;
        spans = inputs.Spans;
        cycles = inputs.Cycles;
        configuration = inputs.Configuration;
        this.report = report;
        this.unchanged = unchanged ?? (static _ => false);
        this.conditions = conditions;
    }

    /// <summary>
    /// Gets a value indicating whether this evaluator reports the problems it finds, which only
    /// the pass over the symbols, a check of an expression and a build's conditions do.
    /// </summary>
    private bool Reporting => mode != EvaluationMode.Query;

    /// <summary>
    /// Assigns every symbol in <paramref name="symbols"/> its kind, value and address size,
    /// reporting each problem it finds into <paramref name="owned"/> with the file of the symbol
    /// that found it.
    /// When <paramref name="unchanged"/> reports that a symbol belongs to an unchanged file, its
    /// value is read as it stands rather than evaluated again.
    /// </summary>
    /// <returns>The symbols of unchanged files whose values were read.</returns>
    public static IReadOnlySet<Symbol> EvaluateSymbols(
        SegmentTable segments,
        IReadOnlyList<Symbol> symbols,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<(Diagnostic Diagnostic, string Owner)> owned,
        Func<Symbol, bool>? unchanged,
        Func<string, long?>? binaryLength,
        Configuration? configuration = null)
    {
        var inputs = new EvaluationInputs(segments, new BoundNames(resolved))
        {
            BinaryLength = binaryLength,
            Configuration = configuration,
        };
        var evaluator = new Evaluator(
            EvaluationMode.Report, inputs, (diagnostic, owner) => owned.Add((diagnostic, owner)), unchanged);
        foreach (var symbol in symbols)
            evaluator.EvaluateSymbol(symbol);
        return evaluator.unchangedReads;
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> and reports any problems with it. An
    /// expression that is not a symbol's value, such as an operand of a data directive, is never
    /// reached by the pass over the symbols, so the code that reads it calls this to check it.
    /// </summary>
    public static void Check(
        SyntaxNode expression,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        List<Diagnostic> diagnostics,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null,
        Configuration? configuration = null)
    {
        var inputs = new EvaluationInputs(segments, new BoundNames(resolved, bound))
        {
            BinaryLength = binaryLength,
            Spans = spans,
            Cycles = cycles,
            Configuration = configuration,
        };
        new Evaluator(EvaluationMode.Check, inputs, (diagnostic, _) => diagnostics.Add(diagnostic)).Bytes(expression);
    }

    /// <summary>
    /// Returns the value of an expression once every symbol has been evaluated. Nothing is
    /// reported from here, because this answers an editor's query about a file whose problems
    /// have all been reported already.
    /// </summary>
    public static Value ValueOf(
        SyntaxNode expression,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null,
        Configuration? configuration = null) =>
        Querying(new EvaluationInputs(segments, new BoundNames(resolved, bound))
        {
            Spans = spans,
            Cycles = cycles,
            Configuration = configuration,
        }).Evaluate(expression);

    /// <summary>
    /// Returns the value a <c>.select</c> or a <c>.switch</c> chooses once every symbol has been
    /// evaluated, or null when <paramref name="node"/> is neither or what decides the choice is not
    /// a constant. Nothing is reported from here.
    /// </summary>
    public static SyntaxNode? ChosenOf(
        SyntaxNode node,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null) =>
        Querying(new EvaluationInputs(segments, new BoundNames(resolved, bound)) { Configuration = configuration })
            .ChosenBy(node);

    /// <summary>
    /// Creates an evaluator for the conditions of a build, which are evaluated before any
    /// declaration exists. Nothing resolves in a condition, so the evaluator carries no symbols
    /// at all, and <paramref name="conditions"/> decides what a name and a call may mean there.
    /// </summary>
    public static Evaluator ForConditions(Conditions conditions, List<Diagnostic> diagnostics) =>
        new(EvaluationMode.Conditions,
            new EvaluationInputs(
                SegmentTable.Standard, new BoundNames(ReadOnlyDictionary<(SyntaxTree, int), Symbol>.Empty)),
            (diagnostic, _) => diagnostics.Add(diagnostic),
            conditions: conditions);

    /// <summary>
    /// Returns the value of an expression. A caller that reports problems, such as the pass over
    /// the symbols or the conditions of a build, evaluates through this method. A caller that
    /// only queries goes through <see cref="ValueOf"/>.
    /// </summary>
    public Value Evaluate(SyntaxNode node) =>
        context.Declaring is null ? Evaluated(node) : CheckedForCa65(node, Evaluated(node));

    /// <summary>
    /// Returns an evaluator that answers a query, reporting nothing and changing no symbol.
    /// </summary>
    private static Evaluator Querying(EvaluationInputs inputs) =>
        new(EvaluationMode.Query, inputs, static (_, _) => { });

    private static bool InsideCall(SyntaxNode node, SyntaxNode top)
    {
        for (var at = node.Parent; at is not null && at != top.Parent; at = at.Parent)
        {
            if (at is CallExpressionSyntax)
                return true;
        }
        return false;
    }

    /// <summary>Returns the diagnostic message for a value too wide for ca65 to hold.</summary>
    private static DiagnosticMessage TooWide(long number) => Catalogue.NumberTooWide.Message(Value.Of(number));

    /// <summary>
    /// Determines whether ca65 can hold a value. ca65's own arithmetic is 32-bit signed, and it
    /// also reads a 32-bit value as unsigned.
    /// </summary>
    private static bool FitsCa65(long value) => value is >= -0x80000000L and <= 0xffffffffL;

    /// <summary>
    /// Returns the word that one side of a comparison gives. This is the value when it is
    /// already a word, or otherwise the bare name on that side, which is compared as a word
    /// without being looked up.
    /// </summary>
    private static string? WordOf(Value value, ExpressionSyntax expression) => value.IsWord
        ? value.Text
        : expression is NameExpressionSyntax { SimpleName: { } word }
            ? word.Text
            : null;

    private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

    /// <summary>Evaluates an operand for its bytes, or for its value when it has no bytes.</summary>
    private void Bytes(SyntaxNode operand)
    {
        if (BytesIn(operand) is not null)
            return;

        // A named scope is a namespace and has no address of its own, so nothing is emitted
        // for it and ca65 would find the name undefined. A name inside a call is left for the
        // call to check, since some calls accept a scope (such as `.spanof` of a scope).
        foreach (var node in (IEnumerable<SyntaxNode>)[operand, .. operand.DescendantNodes()])
        {
            if (node is NameExpressionSyntax name && !InsideCall(name, operand)
                && SymbolOf(name) is { Kind: SymbolKind.Scope } scope && name.LastPart?.Name.Text == scope.Name)
                Report(name, Catalogue.ScopeHasNoAddress.Message(scope.Name));
        }
        Evaluate(operand);
    }

    /// <summary>
    /// Checks a value on its way into the output against what ca65 can hold, and returns it.
    /// ca65 computes in 32 bits, and the output emits a declaration's expression as it appears in
    /// the source, so every step of the expression has to be a number that ca65 can also reach.
    /// </summary>
    private Value CheckedForCa65(SyntaxNode node, Value value)
    {
        // A name's value comes from its declaration, and a value too wide for ca65 is reported
        // there rather than again at every use of the name.
        if (node is NameExpressionSyntax || context.Declaring is not { } within)
            return value;
        if (value.AsNumber() is { } number && !FitsCa65(number))
        {
            problems++;
            if (wide.Add(within))
                Report(node, TooWide(number));
        }
        return value;
    }

    /// <summary>
    /// Reports a literal that contains a character above <c>$7f</c>, unless a charmap or layout
    /// handles it. Outside a charmap, text is ASCII and <c>\xHH</c> produces any byte, so a character typed
    /// directly above <c>$7f</c> is an error rather than a byte of some encoding. A charmap entry
    /// is where such a character is given a byte. Text in a data declaration is checked where its
    /// bytes are laid out, because that is where the charmap applied to it is known. Everything
    /// else, such as a constant, an operand or a condition, is reported here.
    /// </summary>
    private void CheckAscii(LiteralExpressionSyntax literal)
    {
        if (!literal.Token.Text.Any(c => c > 127) || InCharmapOrData(literal) || !outsideAscii.Add(literal))
            return;
        Report(literal, Catalogue.TextNotAscii);
    }

    /// <summary>
    /// Determines whether the literal is in a place where a charmap maps it or where layout
    /// checks it. Text that a call builds from the literal is checked here, because layout sees
    /// only the call's result.
    /// </summary>
    private bool InCharmapOrData(SyntaxNode literal)
    {
        var built = false;
        for (var node = literal.Parent; node is not null; node = node.Parent)
        {
            if (node is CharmapEntrySyntax)
                return true;
            if (node is DataDirectiveSyntax or DataValuesSyntax)
                return !built;
            if (node is CallExpressionSyntax { Callee: { } callee } && SymbolOf(callee)?.Kind == SymbolKind.Charmap)
                return true;
            if (node is CallExpressionSyntax)
                built = true;
        }
        return false;
    }

    private Value Evaluated(SyntaxNode node)
    {
        // A literal the lexer rejected has no value, just as an undeclared name has none. Its
        // problem has been reported once, where it appears, and reading it for a value would
        // mean reading digits that are not digits.
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

            case BinaryExpressionSyntax binary when IsIn(binary.OperatorToken):
                return In(binary);

            // A set stands only where `.in` and `.switch` read it as one, and never has a value.
            case SetExpressionSyntax set:
                Report(set, Catalogue.SetOutOfPlace);
                return Value.Unknown;

            case BinaryExpressionSyntax binary:
                // `&&` and `||` skip the right operand once the left decides the result, so
                // `.defined(TRACE) && TRACE` has a value when TRACE is not defined, and the
                // name on the right is never looked up.
                var op = binary.OperatorToken;
                var first = Evaluate(binary.Left);
                if (first.AsNumber() is { } decided && Operators.ShortCircuits(op.Kind, decided))
                    return Value.Of(decided != 0);
                var second = Evaluate(binary.Right);

                // A `one` parameter and a repetition over words compare as words. The side
                // that is not already a word is the bare name beside it, which is treated as a
                // word rather than a name and is never looked up.
                if (op.Kind is SyntaxKind.EqualsEquals or SyntaxKind.BangEquals
                    && (first.IsWord || second.IsWord)
                    && WordOf(first, binary.Left) is { } left && WordOf(second, binary.Right) is { } right)
                {
                    var same = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                    return Value.Of(op.Kind == SyntaxKind.EqualsEquals ? same : !same);
                }

                // Two locations in one data declaration are a known distance apart wherever
                // the declaration lands.
                if (op.Kind == SyntaxKind.Minus && (first.Kind == ValueKind.Unknown || second.Kind == ValueKind.Unknown)
                    && Distance(binary) is { } distance)
                {
                    return Value.Of(distance);
                }
                return Binary(op, first, second);

            case CallExpressionSyntax call:
                return Call(call);

            // The remaining cases are `*`, an error the parser has already reported, and the
            // CPU names, which only `.cpu` and `.target` accept.
            default:
                return Value.Unknown;
        }
    }

    /// <summary>
    /// Evaluates <paramref name="symbol"/> unless it has been evaluated already. Only the pass
    /// over the symbols does this. By the time anything else asks, every symbol has been evaluated
    /// and every type laid out, and neither a query nor a check may modify a symbol that another
    /// thread is reading.
    /// </summary>
    private void EnsureEvaluated(Symbol symbol)
    {
        if (mode == EvaluationMode.Report)
            EvaluateSymbol(symbol);
    }

    private void EvaluateSymbol(Symbol symbol)
    {
        if (unchanged(symbol))
        {
            unchangedReads.Add(symbol);
            return;
        }
        using (Enter(context with { Owner = symbol }))
            EvaluateOwnSymbol(symbol);
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

        // The type that a `.type T` names is worth keeping on the symbol. Emission walks into
        // it, an editor asks what a path reaches through it, and nothing else would have
        // resolved it unless a path happened to lead that way.
        symbol.Type ??= symbol.TypeExpression is NameExpressionSyntax typed ? SymbolOf(typed) : null;

        switch (symbol.Kind)
        {
            // A layout assigns its members their offsets and sizes, and takes its own size
            // from them. Evaluating a member first evaluates the type that holds it.
            case SymbolKind.Struct:
            case SymbolKind.Union:
                using (Evaluating(symbol))
                    LayOut(symbol);
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
            case SymbolKind.Func when symbol.Items.Count > 0 && Reporting:
                using (Evaluating(symbol, context with { ReadingBody = true }))
                    Evaluate(symbol.Items[0]);
                return;
            case SymbolKind.Constant when symbol.FollowsPrevious:
                symbol.Value = Number(Follows(symbol));
                if (symbol.Value.AsNumber() is { } counted && !FitsCa65(counted) && wide.Add(symbol))
                    Report(symbol.DeclarationSpan, TooWide(counted), []);
                return;
            default:
                break;
        }

        // Data found elsewhere with no element of its own takes the element of the data its
        // address names, before it is sized from that element.
        if (symbol is { Kind: SymbolKind.AddressAlias, ValueExpression: { Parent: DataDeclarationSyntax elsewhere } address })
            TakeElement(symbol, elsewhere, address);

        // A data declaration takes its size and element count from what it declares, and these
        // are what `.sizeof` and `.countof` return for it. Mixed data has bytes and no elements.
        // An import that declares an element type is sized from it in exactly the same way,
        // because nt65 works with what the import declares, as it does with a routine import's
        // signature.
        if (symbol.Kind == SymbolKind.Data || symbol.IsTypedStorage)
        {
            // How much room a declaration takes is computed with nt65's own arithmetic, even
            // while another symbol's value is being evaluated. The output contains only the
            // resulting count, in a `.res`, so its steps are not checked against what ca65 can
            // hold.
            using (Evaluating(symbol, context with { Declaring = null }))
            {
                if (symbol.Data is { } element && RoomFor(element) is { } room)
                {
                    symbol.Size = room.Bytes;
                    symbol.Count = room.Elements;
                }
                else if (symbol.Definition is BlockSyntax block)
                {
                    symbol.Size = RoomForMixed(block);
                }
            }
        }

        if (symbol.ValueExpression is not { } expression)
        {
            // A label, a routine or a data declaration has its address where it lands, which
            // only the linker knows. Its address size comes from the segment it is in.
            symbol.AddressSize = symbol.Kind switch
            {
                SymbolKind.Label or SymbolKind.Proc or SymbolKind.Data => SegmentSize(symbol.Segment),
                _ => symbol.AddressSize,
            };
            return;
        }

        // The output contains the symbol's value and every step of the expression that
        // defines it, because ca65 computes those steps again from the text.
        using (Evaluating(symbol, context with { Declaring = symbol }))
            symbol.Value = Evaluate(expression);

        // `NAME = expr` is a constant if the expression names no address, and an address
        // alias if it does. Imports and extern procs are already classified.
        // A distance between two places in one data declaration names addresses and is a
        // constant all the same, because nt65 has its value.
        if (symbol.Kind == SymbolKind.Constant && symbol.Value.AsNumber() is null && NamesAnAddress(expression))
        {
            // An enum is a set of numbers, and a member that stood for an address would give
            // every member after it a value that cannot be computed.
            if (symbol.IsEnumMember)
            {
                Report(expression, Catalogue.EnumMemberIsNotAnAddress.Message(symbol.Name));
                symbol.Value = Value.Unknown;
                return;
            }

            // A `.const` is never an address, though it may be a distance only the linker knows.
            // The name is an alias from here on either way, so that what uses it is not reported
            // again.
            if (expression.Parent is ConstantDeclarationSyntax { Keyword.IsMissing: false } && IsAddressValued(expression))
                Report(symbol.DeclarationSpan, Catalogue.ConstantNamesAnAddress.Message(symbol.Name), []);
            symbol.Kind = SymbolKind.AddressAlias;
        }
        if (symbol.Kind != SymbolKind.ImportedAddress)
            symbol.AddressSize = SizeOf(expression, symbol.Segment, symbol.Value);
    }

    /// <summary>
    /// Gives data found elsewhere the element of the data its address names, when it writes none,
    /// and checks that an element it writes is that data's. An address that is not the name of
    /// data needs an element of its own.
    /// </summary>
    private void TakeElement(Symbol symbol, DataDeclarationSyntax declaration, ExpressionSyntax address)
    {
        var target = address is NameExpressionSyntax name ? SymbolOf(name) : null;
        if (target is not null && target != symbol)
            EnsureEvaluated(target);
        if (target is not { IsTypedStorage: true, Data: DataDirectiveSyntax element } || target == symbol)
        {
            if (declaration.Directive is null)
                Report(symbol.DeclarationSpan, Catalogue.DataElsewhereNeedsAnElement.Message(symbol.Name), []);
            return;
        }
        if (declaration.Directive is not { } written)
        {
            symbol.Data = element;
            symbol.Type = target.Type;
            return;
        }
        if (!string.Equals(written.Directive.Text, element.Directive.Text, StringComparison.OrdinalIgnoreCase)
            || symbol.Type is { } type && target.Type is { } other && type != other)
        {
            Report(symbol.DeclarationSpan, Catalogue.DataElsewhereElementDiffers.Message(
                symbol.Name, written.GetText().Trim(), target.Name, element.GetText().Trim()), []);
        }
    }

    /// <summary>
    /// Reports a cycle once, naming the rest of the ring. Every symbol on the ring is left without
    /// a value, and none of them reports again. The cycle is reported at the declaration that
    /// comes first in the program, by file and then by position, and the ring is named from
    /// there. The symbol through which evaluation happened to reach the ring must not change the
    /// report.
    /// </summary>
    private void ReportCycle(int index)
    {
        var found = evaluating[index..];
        var first = found.IndexOf(found
            .OrderBy(symbol => symbol.Tree.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.NameSpan.Start)
            .First());
        List<Symbol> ring = [.. found.Skip(first), .. found.Take(first)];

        // Every symbol on the ring is left without a value, and a type on the ring without a
        // layout. Code that walks into a type later has to be able to tell. Only the symbols this
        // pass evaluates are marked. A function of a file that is not read again can be on a
        // ring that a call reaches, and it keeps what its own evaluation found, as does every
        // symbol a check reaches once the program is complete.
        foreach (var member in ring)
        {
            if (mode == EvaluationMode.Report && !unchanged(member))
                member.IsCyclic = true;
        }
        var symbol = ring[0];
        Report(symbol.DeclarationSpan, Catalogue.DefinedInTermsOfItself.Message(symbol.DisplayName),
            [.. ring.Skip(1).Select(other =>
                new RelatedSpan(other.DeclarationSpan, $"through `{other.DisplayName}`"))]);
    }

    /// <summary>
    /// Returns the value of a name. Inside a function body, a parameter has the value of the
    /// argument it was called with. A member reached through a path has the sum of the offsets
    /// along that path, which makes <c>Player::pos::y</c> a number.
    /// </summary>
    private Value ValueOfName(NameExpressionSyntax name)
    {
        if (mode == EvaluationMode.Conditions)
            return InCondition(name, conditions!);
        if (names.BoundItem(name) is { } item)
            return Indexed(name, Evaluate(item));
        if (SymbolOf(name) is not { } symbol)
        {
            // Binding did not report unresolved names in the values `.select` chooses between;
            // they are reported here, once evaluation has chosen the value they are in.
            if (context.Choosing > 0 && name.SimpleName is { Kind: SyntaxKind.Identifier or SyntaxKind.CheapLocal } alone)
                Report(alone, Catalogue.NotDeclared.Message(alone.Text, ""));
            return Value.Unknown;
        }
        // A binding that walks an enum stands for the member itself, whose value is requested
        // now. The value copied when the binding was made is unknown whenever the enum's file
        // has not been evaluated yet, and which files have been evaluated depends on their
        // order.
        if (names.Members.TryGetValue(symbol, out var member))
            return Indexed(name, ValueOfSymbol(member));
        if (names.Values.TryGetValue(symbol, out var argument))
            return Indexed(name, argument);
        return Indexed(name, symbol.Kind == SymbolKind.Member ? OffsetAlong(name) : ValueOfSymbol(symbol));
    }

    /// <summary>
    /// Returns <paramref name="value"/> advanced past the elements that any <c>[i]</c> along the
    /// path steps over. The value is a member's offset when the path runs through a type, and an
    /// address when the path starts at data. Every name a path can end in comes through here, so
    /// an index on a name that reaches no declaration is rejected rather than silently dropped.
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
    /// Returns the number of bytes that the indexes along a path add up to, or null when one of
    /// them is invalid, which is reported here. An index is computed before the program runs, so
    /// it has to be a constant and select an element the declaration holds.
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
                Report(index, Catalogue.NotIndexable.Message(
                    symbol.DisplayName,
                    symbol is { Kind: SymbolKind.Data, Data: null }
                        ? "mixed data, which has no elements"
                        : $"{symbol.KindPhrase}, not an array"));
                return null;
            }

            // A count that nt65 cannot compute has already been reported where it appears.
            EnsureEvaluated(symbol);
            if (symbol.Count is not { } count || ElementIndexes.Stride(symbol) is not { } stride)
                return null;

            // Empty brackets leave a missing index with no text, and it has already been
            // reported where the brackets are.
            var indexExpression = index.Index;
            if (indexExpression.Span.Length == 0)
                return null;
            if (Evaluate(indexExpression).AsNumber() is not { } at)
            {
                // A name in the index that does not resolve has already been reported where it
                // appears, and it is the only reason the index is not constant, so nothing more
                // is reported.
                if (Names(indexExpression))
                {
                    Report(indexExpression, Catalogue.ElementIndexNotConstant.Message(symbol.DisplayName));
                }
                return null;
            }
            if (at < 0 || at >= count)
            {
                Report(indexExpression, Catalogue.ElementIndexOutOfRange.Message(at < 0
                    ? $"element index {at} is negative; indexes start at 0"
                    : $"index {at} is past the end of `{symbol.DisplayName}`: it holds {count} "
                        + $"{(count == 1 ? "element" : "elements")}, so the last index is {count - 1}"));
                return null;
            }
            offset += at * stride;
        }
        return offset;
    }

    /// <summary>
    /// Returns the offset that a path of members adds up to. A type contributes nothing, and a
    /// member contributes its own offset. A path that starts at an instance is an address, which
    /// only the linker knows, so it has no value here and is emitted symbolically instead.
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
            EnsureEvaluated(part);
            if (part.Value.AsNumber() is not { } own)
                return Value.Unknown;
            offset += own;
        }
        return Value.Of(offset);
    }

    /// <summary>
    /// Determines whether every name in an expression refers to something, either declared or
    /// bound.
    /// </summary>
    private bool Names(SyntaxNode node) =>
        (node is not NameExpressionSyntax name || SymbolOf(name) is not null || names.BoundItem(name) is not null)
        && node.ChildNodes.All(Names);

    /// <summary>
    /// Returns the value of an enum member with no value of its own, which is the previous
    /// member's value plus one, or zero for the first member. Returns null when the previous
    /// member has no value.
    /// </summary>
    private long? Follows(Symbol member)
    {
        if (member.PreviousMember is not { } previous)
            return 0;
        EvaluateSymbol(previous);
        return previous.Value.AsNumber() is { } before ? before + 1 : null;
    }

    /// <summary>
    /// Returns the value of a symbol, evaluating its declaration first if needed. A label has no
    /// value at all, because only the linker knows where it lands.
    /// </summary>
    private Value ValueOfSymbol(Symbol symbol)
    {
        EnsureEvaluated(symbol);
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

    /// <summary>
    /// Reports an operand that is a string where a number belongs, since there is no string
    /// arithmetic, and returns an unknown value.
    /// </summary>
    private Value Reject(SyntaxToken op, Value operand)
    {
        if (operand.IsString)
            Report(op, Catalogue.OperatorOnText.Message(op.Text));
        return Value.Unknown;
    }

    /// <summary>
    /// Returns the symbol a name refers to, and reports a path through a repetition binding that
    /// names nothing in the current iteration.
    /// </summary>
    private Symbol? SymbolOf(NameExpressionSyntax name)
    {
        var symbol = names.SymbolOf(name, out var problem);
        if (problem is var (at, message))
            Report(at, message);
        return symbol;
    }

    /// <summary>
    /// Returns the symbol that a node refers to when the node is a name, or null otherwise,
    /// because only a name can refer to a symbol.
    /// </summary>
    private Symbol? SymbolOf(SyntaxNode node) => node is NameExpressionSyntax name ? SymbolOf(name) : null;

    private void Report(Span span, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related) =>
        Add(new Diagnostic(span, Severity.Error, message, related));

    private void Report(SyntaxToken token, DiagnosticMessage message) =>
        Add(new Diagnostic(token.Parent.Tree.GetSpan(token.Span), Severity.Error, message, []));

    private void Report(SyntaxNode node, DiagnosticMessage message) =>
        Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message, []));

    private void Add(Diagnostic diagnostic)
    {
        problems++;
        report(diagnostic, context.Owner?.Tree.Path ?? diagnostic.Span.File);
    }

    /// <summary>
    /// Replaces the walk context with <paramref name="inner"/> until the returned scope is
    /// disposed, which restores the context it replaced.
    /// </summary>
    private ContextScope Enter(WalkContext inner)
    {
        var outer = context;
        context = inner;
        return new ContextScope(this, outer, Pushed: false);
    }

    /// <summary>
    /// Adds <paramref name="symbol"/> to the symbols being evaluated until the returned scope is
    /// disposed, so that a path back to it is found as a cycle.
    /// </summary>
    private ContextScope Evaluating(Symbol symbol) => Evaluating(symbol, context);

    /// <summary>
    /// Adds <paramref name="symbol"/> to the symbols being evaluated and replaces the walk context
    /// with <paramref name="inner"/> until the returned scope is disposed, which undoes both.
    /// </summary>
    private ContextScope Evaluating(Symbol symbol, WalkContext inner)
    {
        evaluating.Add(symbol);
        var outer = context;
        context = inner;
        return new ContextScope(this, outer, Pushed: true);
    }

    /// <summary>
    /// Represents where the walk over the program is. A scope that changes it restores it when
    /// the scope ends, through a <see cref="ContextScope"/>.
    /// </summary>
    /// <param name="Owner">
    /// The symbol whose evaluation is under way at the outermost level, whose file each problem
    /// found belongs to.
    /// </param>
    /// <param name="Declaring">
    /// The symbol whose value expression is being evaluated. The output emits that expression as
    /// it appears in the source, so every step of it is checked against what ca65 can hold.
    /// </param>
    /// <param name="Choosing">
    /// The number of <c>.select</c>-chosen values that evaluation is inside, where an unresolved
    /// name has to be reported.
    /// </param>
    /// <param name="ReadingBody">
    /// Whether a function body is being read with no arguments, when no choice can be made.
    /// </param>
    /// <param name="Apart">
    /// Whether the walk over what a file emits to its segments is under way while computing a
    /// distance between two declarations. A length computed along the way may ask for that walk
    /// again.
    /// </param>
    private readonly record struct WalkContext(
        Symbol? Owner, Symbol? Declaring, int Choosing, bool ReadingBody, bool Apart);

    /// <summary>
    /// Restores the walk context a scope replaced when it is disposed, and removes the symbol the
    /// scope added to the symbols being evaluated, if it added one.
    /// </summary>
    private readonly ref struct ContextScope(Evaluator evaluator, WalkContext outer, bool Pushed)
    {
        public void Dispose()
        {
            evaluator.context = outer;
            if (Pushed)
                evaluator.evaluating.RemoveAt(evaluator.evaluating.Count - 1);
        }
    }
}
