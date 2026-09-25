using System.Diagnostics;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Computes the value of a call to a built-in function, to a charmap applied to text, or to a
/// function the program declares, whose body is read with the arguments in place of its
/// parameters.
/// <para>
/// A built-in is the one place where an expression asks about the program rather than about
/// numbers, so each built-in states for itself what it needs and what it cannot accept. The
/// built-ins that are purely arithmetic are the ones a build's conditions may also call.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Determines whether a built-in is one that the configuration alone can evaluate, which
    /// <see cref="BuiltinFunction.Arithmetic"/> records.
    /// </summary>
    private static bool Answerable(BuiltinKind kind) => kind != BuiltinKind.None && SyntaxFacts.Builtin(kind).Arithmetic;

    /// <summary>
    /// Determines whether a symbol has bytes of its own in the output, which are what
    /// <c>.endof</c> and <c>.spanof</c> measure.
    /// </summary>
    private static bool HasBytesOfItsOwn(Symbol symbol) => symbol.Kind is SymbolKind.Proc or SymbolKind.Data;

    private static Value Number1(Value[] arguments, Func<long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a] ? Value.Of(apply(a.Number)) : Value.Unknown;

    private static Value Number2(Value[] arguments, Func<long, long, long> apply) =>
        arguments is [{ Kind: ValueKind.Number } a, { Kind: ValueKind.Number } b]
            ? Value.Of(apply(a.Number, b.Number))
            : Value.Unknown;

    /// <summary>
    /// Returns the byte a charmap maps <paramref name="character"/> to, or null when no entry
    /// names it. A later entry overrides an earlier one for the characters both name.
    /// </summary>
    private static long? Mapped(List<(long First, long Last, long To)> ranges, long character)
    {
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var (first, last, to) = ranges[i];
            if (character >= first && character <= last)
                return to + (character - first);
        }
        return null;
    }

    /// <summary>
    /// Returns the value of a call to a built-in function, a character mapping applied to text,
    /// or a function declared with <c>.func</c>. A <c>.func</c> call evaluates to its body with
    /// the arguments in place of its parameters.
    /// </summary>
    private Value Call(CallExpressionSyntax call)
    {
        var given = call.Arguments.Arguments;
        if (mode == EvaluationMode.Conditions)
            return InCondition(call, given, conditions!);
        if (call.Callee is { } callee)
            return Applied(callee, given);
        if (call.Function is not { Kind: SyntaxKind.Directive } function)
            return Value.Unknown;

        var kind = call.BuiltinKind;
        if (!Fits(kind, function, given))
            return Value.Unknown;
        switch (kind)
        {
            case BuiltinKind.Select:
                return Select(given);
            case BuiltinKind.Switch:
                return Switch(given);
            case BuiltinKind.Mode or BuiltinKind.Empty:
                return AboutAnArgument(kind, given);

            // Where a segment is loaded and where it runs are known only to the linker, so
            // `.loadof` and `.runof` never have a value here.
            case BuiltinKind.Loadof or BuiltinKind.Runof:
                return Value.Unknown;
            case BuiltinKind.Endof or BuiltinKind.Spanof:
                return Extent(kind, given);
            case BuiltinKind.Mincycles or BuiltinKind.Maxcycles:
                return Cycles(kind, function, given);
            case BuiltinKind.Sizeof or BuiltinKind.Countof:
                return Measured(kind, given);
            case BuiltinKind.Exprof:
                return ExprOf(call, function, given);

            // `.addrsize` asks about the shape of its argument rather than its value.
            case BuiltinKind.Addrsize:
                return SizeOf(given[0], null) is { } size ? Value.Of((long)size) : Value.Unknown;

            // A condition in an expansion asks about the CPU in the same way the build's
            // conditions do.
            case BuiltinKind.Target or BuiltinKind.Has when configuration is not null:
                return Configuration.AboutTheCpu(kind, function, given, configuration.Cpu,
                    (_, message) => Report(function, message));

            // `.target`, `.has`, `.defined` and the three built-ins only a macro body uses
            // (`.mode`, `.empty`, `.exprof`) are handled before this point, each by the pass that
            // knows what they ask about.
            default:
                return Plain(kind, function, given);
        }
    }

    /// <summary>
    /// Checks whether a call gives <paramref name="kind"/> as many arguments as its row of
    /// <see cref="SyntaxFacts.Builtins"/> allows, and reports the call when it does not. The
    /// arguments of an arithmetic built-in are values, so they are still evaluated, and any
    /// problem inside one is still found.
    /// </summary>
    private bool Fits(BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        var builtin = SyntaxFacts.Builtin(kind);
        if (builtin.Accepts(arguments.Count))
            return true;
        if (builtin.Arithmetic)
        {
            foreach (var argument in arguments)
                Evaluate(argument);
        }
        Report(function, kind switch
        {
            BuiltinKind.Select => Catalogue.SelectArguments,
            BuiltinKind.Switch => Catalogue.SwitchArguments,
            BuiltinKind.Target => Catalogue.TargetArgument.Message(CpuNames.Listed),
            BuiltinKind.Has => Catalogue.HasArgument,
            _ => Catalogue.BuiltinArguments.Message(builtin.Name, builtin.Takes!),
        });
        return false;
    }

    /// <summary>
    /// Returns the value of <c>.mode</c> or <c>.empty</c>, which only a macro body uses. Each asks
    /// about an argument rather than a value, so each reads what the parameter was given rather
    /// than evaluating it.
    /// </summary>
    private Value AboutAnArgument(BuiltinKind kind, IReadOnlyList<SyntaxNode> arguments)
    {
        if (names.Argument(arguments[0]) is not { } about)
            return Value.Unknown;
        return kind == BuiltinKind.Mode
            ? about.Operand is { } operand ? Value.Word(Operands.ModeOf(operand)) : Value.Unknown
            : Value.Of(about.Block is null || Macros.LinesOf(about.Block).Count == 0);
    }

    /// <summary>
    /// Returns the value of <c>.endof</c> or <c>.spanof</c>. They describe layout rather than
    /// shape, and are address expressions like any label difference. Only the difference can ever
    /// be a number, because nt65 never knows an absolute address, and only a caller that has laid
    /// out the file can supply it.
    /// </summary>
    private Value Extent(BuiltinKind kind, IReadOnlyList<SyntaxNode> arguments)
    {
        var name = SyntaxFacts.TextOf(kind);
        if (SymbolOf(arguments[0]) is not { } laid)
            return Value.Unknown;
        if (NotAnExtent(laid, name, arguments[0]))
            return Value.Unknown;
        if (!HasBytesOfItsOwn(laid))
        {
            Report(arguments[0], Catalogue.NothingToMeasure.Message(laid.Name, laid.KindPhrase, name));
            return Value.Unknown;
        }
        layoutReads++;
        return kind == BuiltinKind.Spanof && spans?.Invoke(laid) is { } span ? Value.Of(span) : Value.Unknown;
    }

    /// <summary>
    /// Returns the value of <c>.mincycles</c> or <c>.maxcycles</c>, which is what one pass over a
    /// span of code costs. Both ends are positions in one routine, and only a caller that has
    /// laid out the file can count what lies between them.
    /// </summary>
    private Value Cycles(BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        var name = SyntaxFacts.TextOf(kind);
        if (SymbolOf(arguments[0]) is not { } start || SymbolOf(arguments[1]) is not { } end)
            return Value.Unknown;
        foreach (var (at, symbol) in new[] { (arguments[0], start), (arguments[1], end) })
        {
            if (symbol.Kind is not (SymbolKind.Label or SymbolKind.Proc))
            {
                Report(at, Catalogue.CyclesNeedsAPosition.Message(symbol.DisplayName, symbol.KindPhrase));
                return Value.Unknown;
            }
        }
        layoutReads++;
        if (cycles?.Invoke(start, end, kind == BuiltinKind.Maxcycles) is not { } counted)
            return Value.Unknown;
        if (counted.Problem is { } problem)
        {
            Report(function, Catalogue.CyclesSpanHasNoBound.Message(name, problem));
            return Value.Unknown;
        }
        return counted.Value is { } number ? Value.Of(number) : Value.Unknown;
    }

    /// <summary>
    /// Returns the value of <c>.sizeof</c> or <c>.countof</c>, which measure a declaration's shape.
    /// </summary>
    private Value Measured(BuiltinKind kind, IReadOnlyList<SyntaxNode> arguments)
    {
        var name = SyntaxFacts.TextOf(kind);
        if (SymbolOf(arguments[0]) is not { } measured)
            return Value.Unknown;

        // `.countof(p)` of a `list` parameter is the number of arguments the call gave it.
        if (kind == BuiltinKind.Countof && names.Argument(arguments[0]) is { Parameter.Kind: ParameterKind.List } listed)
            return Value.Of(listed.Items.Count);
        if (NotAnExtent(measured, name, arguments[0]))
            return Value.Unknown;

        // An enum counts its members.
        if (kind == BuiltinKind.Countof && measured.Kind == SymbolKind.Enum)
            return Value.Of(measured.Body?.Symbols.Count(member => member.Kind == SymbolKind.Constant) ?? 0);

        // A routine and mixed data are measured in bytes, not elements. How many bytes a
        // routine takes is a matter of layout, which only a caller that has laid out the
        // file knows.
        var bytesOnly = measured.Kind == SymbolKind.Proc || measured is { Kind: SymbolKind.Data, Data: null };
        if (kind == BuiltinKind.Countof && bytesOnly)
        {
            Report(arguments[0], Catalogue.CountofHasNoElements.Message(
                measured.Name, (measured.Kind == SymbolKind.Proc ? "a routine" : "mixed data"), measured.Name));
            return Value.Unknown;
        }
        if (measured.Kind == SymbolKind.Proc)
        {
            layoutReads++;
            return spans?.Invoke(measured) is { } body ? Value.Of(body) : Value.Unknown;
        }

        EnsureEvaluated(measured);
        var room = kind == BuiltinKind.Sizeof ? measured.Size : measured.Count;
        if (room is null && measured is { Kind: SymbolKind.Data, Data: null })
        {
            Report(arguments[0], Expands(measured)
                ? Catalogue.SizeofDependsOnExpansion.Message(measured.Name, measured.Name)
                : Catalogue.SizeofDependsOnAlignment.Message(measured.Name, measured.Name));
        }
        return room is { } number ? Value.Of(number) : Value.Unknown;
    }

    /// <summary>
    /// Returns the value of <c>.exprof(p)</c>, the expression inside the operand that the call
    /// passed as <c>p</c>, such as <c>5</c> for <c>{#5}</c> or <c>ptr</c> for <c>{(ptr),y}</c>. It
    /// lets a body put an operand's value in data.
    /// </summary>
    private Value ExprOf(CallExpressionSyntax call, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        if (names.ExprOf(call) is { } inner)
            return Evaluate(inner);

        // Outside an expansion the parameter has been given no operand yet, which is not an
        // error.
        if (names.Parameter(arguments[0]) is not { Parameter.Kind: ParameterKind.Operand })
        {
            var exprOf = SyntaxFacts.Builtin(BuiltinKind.Exprof);
            Report(function, Catalogue.BuiltinArguments.Message(exprOf.Name, exprOf.Takes!));
        }
        return Value.Unknown;
    }

    /// <summary>
    /// Evaluates the built-in functions that are arithmetic on their arguments and ask nothing
    /// about the program. These are the ones a build's conditions may also call, which
    /// <see cref="BuiltinFunction.Arithmetic"/> records. Any other built-in that reaches here has
    /// no value, though its arguments are still evaluated.
    /// </summary>
    private Value Plain(BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        var values = arguments.Select(Evaluate).ToArray();
        return kind switch
        {
            BuiltinKind.Sqrt or BuiltinKind.Muldiv or BuiltinKind.Sin or BuiltinKind.Cos => Worked(kind, function, values),
            BuiltinKind.Strsub or BuiltinKind.Strcat => Built(kind, function, arguments, values),
            BuiltinKind.Lobyte => Number1(values, v => v & 0xff),
            BuiltinKind.Hibyte => Number1(values, v => (v >> 8) & 0xff),
            BuiltinKind.Bankbyte => Number1(values, v => (v >> 16) & 0xff),
            BuiltinKind.Loword => Number1(values, v => v & 0xffff),
            BuiltinKind.Hiword => Number1(values, v => (v >> 16) & 0xffff),
            BuiltinKind.Min => Number2(values, Math.Min),
            BuiltinKind.Max => Number2(values, Math.Max),
            BuiltinKind.Strlen => values is [{ Kind: ValueKind.String, Text: { } s }] ? Value.Of(s.Length) : Value.Unknown,
            BuiltinKind.Strat => values is [{ Kind: ValueKind.String, Text: { } t }, { Kind: ValueKind.Number } at]
                && at.Number >= 0 && at.Number < t.Length
                ? Value.Of(t[(int)at.Number])
                : Value.Unknown,

            // Every arithmetic built-in has a case above, so a build's condition never calls one
            // that has no value here.
            _ when Answerable(kind) => throw new UnreachableException($"`{SyntaxFacts.TextOf(kind)}` has no arithmetic."),
            _ => Value.Unknown,
        };
    }

    /// <summary>
    /// Evaluates the built-ins that build text. <c>.strsub(s, start, count)</c> returns part of a
    /// text, and <c>.strcat(part, ...)</c> joins texts and bytes. Text is a string of bytes, so a
    /// number joined in must fit in one byte and is appended as that byte. A part that reaches
    /// outside the text is rejected rather than cut to fit. An argument with no known value leaves
    /// the result unknown, as happens when a function's body is read before it is given any
    /// arguments.
    /// </summary>
    private Value Built(BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments, Value[] values)
    {
        var name = SyntaxFacts.TextOf(kind);
        if (kind == BuiltinKind.Strsub)
        {
            if (values[0].Kind is ValueKind.Number or ValueKind.Word
                || values[1].Kind is ValueKind.String or ValueKind.Word
                || values[2].Kind is ValueKind.String or ValueKind.Word)
            {
                Report(function, Catalogue.BuiltinArguments.Message(name, SyntaxFacts.Builtin(kind).Takes!));
                return Value.Unknown;
            }
            if (values is not [{ Kind: ValueKind.String, Text: { } whole }, { Kind: ValueKind.Number } from,
                { Kind: ValueKind.Number } taken])
            {
                return Value.Unknown;
            }
            if (from.Number < 0 || taken.Number < 0 || from.Number > whole.Length || taken.Number > whole.Length - from.Number)
            {
                Report(function, Catalogue.StrsubOutOfRange.Message(
                    $"{taken.Number} {(taken.Number == 1 ? "byte" : "bytes")} from {from.Number}",
                    $"{whole.Length} {(whole.Length == 1 ? "byte" : "bytes")} long"));
                return Value.Unknown;
            }
            return Value.Of(whole.Substring((int)from.Number, (int)taken.Number));
        }

        var joined = new System.Text.StringBuilder();
        var known = true;
        for (var i = 0; i < values.Length; i++)
        {
            switch (values[i])
            {
                case { Kind: ValueKind.String, Text: { } text }:
                    joined.Append(text);
                    break;
                case { Kind: ValueKind.Number, Number: >= 0 and <= 0xff } one:
                    joined.Append((char)one.Number);
                    break;
                case { Kind: ValueKind.Number } wide:
                    Report(arguments[i], Catalogue.StrcatNotAByte.Message(wide));
                    known = false;
                    break;
                case { Kind: ValueKind.Word }:
                    Report(arguments[i], Catalogue.BuiltinArguments.Message(name, "texts and numbers"));
                    known = false;
                    break;
                default:
                    known = false;
                    break;
            }
        }
        return known ? Value.Of(joined.ToString()) : Value.Unknown;
    }

    /// <summary>
    /// Evaluates the built-ins that compute a number, which are <c>.sqrt</c> for a square root,
    /// <c>.muldiv</c> for a scaled product, and <c>.sin</c> and <c>.cos</c> for building tables.
    /// Each takes whole numbers and returns a whole number. Each reports arguments it cannot
    /// compute a result for, rather than silently giving no value.
    /// </summary>
    private Value Worked(BuiltinKind kind, SyntaxToken function, Value[] values)
    {
        var name = SyntaxFacts.TextOf(kind);
        if (values.Any(value => value.Kind != ValueKind.Number))
            return Value.Unknown;
        long? worked;
        switch (kind)
        {
            case BuiltinKind.Sqrt when values is [var n]:
                worked = IntegerMath.Sqrt(n.Number);
                if (worked is null)
                    Report(function, Catalogue.SqrtOfANegative.Message(n.Number));
                break;
            case BuiltinKind.Muldiv when values is [var a, var b, var c]:
                if (c.Number == 0)
                {
                    Report(function, Catalogue.DivisionByZero);
                    return Value.Unknown;
                }
                worked = IntegerMath.MulDiv(a.Number, b.Number, c.Number);
                if (worked is null)
                    Report(function, Catalogue.ArithmeticOverflow.Message($"`{name}`"));
                break;
            case BuiltinKind.Sin or BuiltinKind.Cos when values is [var angle, var turn, var scale]:
                if (!IntegerMath.InRange(turn.Number, scale.Number))
                {
                    Report(function, Catalogue.TurnOrScaleOutOfRange.Message(name, IntegerMath.Limit));
                    return Value.Unknown;
                }
                worked = kind == BuiltinKind.Sin
                    ? IntegerMath.Sin(angle.Number, turn.Number, scale.Number)
                    : IntegerMath.Cos(angle.Number, turn.Number, scale.Number);
                break;
            default:
                throw new UnreachableException($"`{name}` was given a number of arguments it does not take.");
        }
        return worked is { } number ? Value.Of(number) : Value.Unknown;
    }

    /// <summary>
    /// Returns the value of a name in a build's condition, where the configuration alone decides
    /// it. A name it does not decide is passed on with the reason, rather than looked up among the
    /// declarations, because a check about the program is an <c>.assert</c>, which is evaluated
    /// once the program is known.
    /// </summary>
    private static Value InCondition(NameExpressionSyntax name, Conditions asked)
    {
        if (asked.Name(name) is not { } decided)
            return Value.Unknown;
        if (decided.Why is { } why)
            asked.Undecided(name, name.GetText().Trim(), why);
        return decided.Value;
    }

    /// <summary>
    /// Returns the value of a call in a build's condition. The built-ins that the configuration
    /// alone can evaluate have a value there, and so does a function the configuration decides,
    /// called with arguments it decides. A built-in that measures the program has no answer yet.
    /// </summary>
    private Value InCondition(CallExpressionSyntax call, IReadOnlyList<SyntaxNode> given, Conditions asked)
    {
        if (call.Function is not { Kind: SyntaxKind.Directive } function)
        {
            // A `.func` or a charmap called by name. Its arguments are evaluated here, so that
            // what they use is checked as the rest of the condition is.
            var values = given.Select(Evaluate).ToArray();
            if (asked.Call(call, values) is not { } decided)
                return Value.Unknown;
            if (decided.Why is { } why)
                asked.Undecided(call, call.Callee?.GetText().Trim(), why);
            return decided.Value;
        }

        var kind = call.BuiltinKind;
        if (kind is BuiltinKind.Target or BuiltinKind.Has)
        {
            return Fits(kind, function, given)
                ? Configuration.AboutTheCpu(kind, function, given, asked.Cpu, (_, message) => Report(function, message))
                : Value.Unknown;
        }

        // Only the value the condition chooses is read, so it alone has to be decided.
        if (kind == BuiltinKind.Select)
        {
            if (!Fits(kind, function, given))
                return Value.Unknown;
            return Evaluate(given[0]).AsNumber() is { } holds ? Evaluate(given[holds != 0 ? 1 : 2]) : Value.Unknown;
        }
        if (kind == BuiltinKind.Switch)
            return Fits(kind, function, given) ? Switch(given) : Value.Unknown;

        // What the function asks about is checked before its arguments are read, so a
        // `.sizeof(Point)` is one reason, not that and a `Point` the configuration does not decide.
        if (!Answerable(kind))
        {
            asked.Undecided(call, null, Undecided.Measured);
            return Value.Unknown;
        }
        return Fits(kind, function, given) ? Plain(kind, function, given) : Value.Unknown;
    }

    /// <summary>
    /// Evaluates <c>.select(c, a, b)</c>, which is <c>a</c> when the constant <c>c</c> holds and
    /// <c>b</c> when it does not. Only the chosen value is evaluated.
    /// </summary>
    private Value Select(IReadOnlyList<SyntaxNode> arguments)
    {
        var condition = Evaluate(arguments[0]);
        if (condition.AsNumber() is not { } holds)
        {
            // A function's body is read once with no arguments, when its parameters have no
            // values yet. Both values are evaluated then, so that a cycle through either one is
            // found.
            if (context.ReadingBody)
            {
                Evaluate(arguments[1]);
                Evaluate(arguments[2]);
            }
            else if (condition.IsString)
            {
                Report(arguments[0], Catalogue.SelectConditionIsText);
            }
            else
            {
                Report(arguments[0], Catalogue.SelectConditionNotConstant);
            }
            return Value.Unknown;
        }
        using (Enter(context with { Choosing = context.Choosing + 1 }))
            return Evaluate(arguments[holds != 0 ? 1 : 2]);
    }

    /// <summary>
    /// Reports a symbol that <paramref name="function"/> cannot measure, and returns whether it
    /// reported one. A label and a scope look as if they had an extent but have none, because a
    /// label is only a position and a scope only a namespace. An indexed path, and an import that
    /// does not declare what its bytes are, cannot be measured either.
    /// </summary>
    private bool NotAnExtent(Symbol symbol, string function, SyntaxNode at)
    {
        if (at is NameExpressionSyntax { IsIndexed: true })
        {
            Report(at, Catalogue.MeasuresADeclaration.Message(function, at.GetText().Trim()));
            return true;
        }
        if (HasNoElementType(symbol))
        {
            Report(at, Catalogue.DataHasNoElementType.Message(symbol.DisplayName));
            return true;
        }
        var what = symbol.Kind switch
        {
            SymbolKind.Label => "a label, which is only a position",
            SymbolKind.Scope => "a scope, which is only a namespace",

            // An import declares nothing about its shape unless it gives an element type, and
            // nt65 does not read ca65 source to find one.
            SymbolKind.ImportedAddress when symbol.Data is null =>
                "an import that does not declare what its bytes are: `.import name: .byte[n]` declares them",
            _ => null,
        };
        if (what is null)
            return false;
        Report(at, Catalogue.NotMeasurable.Message(symbol.DisplayName, what, function));
        return true;
    }

    /// <summary>
    /// Evaluates a call to a charmap or a function by name. A charmap maps one character to its
    /// byte. A function evaluates its body with each parameter bound to the argument it was
    /// given. A function whose evaluation needs its own value is reported as a cycle, as a
    /// constant's would be.
    /// </summary>
    private Value Applied(NameExpressionSyntax callee, IReadOnlyList<SyntaxNode> given)
    {
        if (SymbolOf(callee) is not { } symbol)
            return Value.Unknown;

        if (symbol.Kind == SymbolKind.Charmap)
        {
            var mapped = Map(symbol);
            return given.Count == 1 && Evaluate(given[0]) is { Kind: ValueKind.Number } character
                && Mapped(mapped, character.Number) is { } b
                ? Value.Of(b)
                : Value.Unknown;
        }

        if (symbol.Kind != SymbolKind.Func || symbol.Items.Count == 0)
            return Value.Unknown;
        if (symbol.ParameterSymbols.Count != given.Count)
        {
            Report(callee, Catalogue.FunctionArgumentCount.Message(
                symbol.Name, symbol.ParameterSymbols.Count, given.Count));
            return Value.Unknown;
        }
        if (evaluating.Contains(symbol))
        {
            ReportCycle(evaluating.IndexOf(symbol));
            return Value.Unknown;
        }

        var values = given.Select(Evaluate).ToArray();

        // A closed function called again with the same arguments gives the result it gave before.
        var kept = KeepsResults ? FunctionResults.For(resolved) : null;
        if (kept is not null && !kept.IsClosed(symbol, IsClosed))
            kept = null;
        if (kept?.Find(symbol, values) is { } known)
            return known;
        var before = problems;

        var bound = names.Values;
        var shadowed = new List<(Symbol Symbol, Value Value, bool Had)>();
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var parameter = symbol.ParameterSymbols[i];
                shadowed.Add((parameter, bound.GetValueOrDefault(parameter), bound.ContainsKey(parameter)));
                bound[parameter] = values[i];
            }
            Value result;
            using (Evaluating(symbol))
                result = Evaluate(symbol.Items[0]);
            if (kept is not null && problems == before)
                kept.Keep(symbol, values, result);
            return result;
        }
        finally
        {
            // The parameters take back what they had before the call, however the call ends.
            foreach (var (parameter, previous, had) in shadowed)
            {
                if (had)
                    bound[parameter] = previous;
                else
                    bound.Remove(parameter);
            }
        }
    }

    /// <summary>
    /// Reads a charmap into the ranges it maps. Each entry maps one character to a value, or a
    /// range of characters to consecutive values. A character that no entry names has no byte,
    /// and applying the mapping to it is an error where that happens. The ranges are kept as
    /// ranges, so a wide one costs no more than a narrow one.
    /// </summary>
    private List<(long First, long Last, long To)> Map(Symbol charmap)
    {
        var ranges = new List<(long First, long Last, long To)>();
        foreach (var line in charmap.Entries)
        {
            if (line is not CharmapEntrySyntax entry)
                continue;
            var first = Evaluate(entry.First).AsNumber();
            var last = entry.Last is { } end ? Evaluate(end).AsNumber() : first;
            var to = Evaluate(entry.Value).AsNumber();
            if (first is null || last is null || to is null || last < first)
                continue;
            ranges.Add((first.Value, last.Value, to.Value));
        }
        return ranges;
    }
}
