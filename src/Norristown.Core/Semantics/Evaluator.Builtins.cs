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
    /// Returns the arguments of a <c>.select</c> call, or null when <paramref name="node"/> is not
    /// a <c>.select</c> call.
    /// </summary>
    internal static IReadOnlyList<SyntaxNode>? SelectArguments(SyntaxNode node) =>
        node is CallExpressionSyntax { Function: { Kind: SyntaxKind.Directive } function } call
            && function.Text.Equals(".select", StringComparison.OrdinalIgnoreCase)
            ? call.Arguments.Arguments
            : null;

    /// <summary>Determines whether a built-in is one that the configuration alone can evaluate.</summary>
    private static bool Answerable(string name) => name is ".lobyte" or ".hibyte" or ".bankbyte"
        or ".loword" or ".hiword" or ".min" or ".max" or ".strlen" or ".strat" or ".strsub" or ".strcat"
        or ".sqrt" or ".muldiv" or ".sin" or ".cos";

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

        var name = function.Text.ToLowerInvariant();
        var arguments = given;

        if (name == ".select")
            return Select(function, arguments);

        // `.mode` and `.empty`, which only a macro body uses, ask about an argument rather than
        // a value, so each reads what the parameter was given rather than evaluating it.
        if (name is ".mode" or ".empty")
        {
            if (arguments.Count != 1 || names.Argument(arguments[0]) is not { } about)
                return Value.Unknown;
            return name == ".mode"
                ? about.Operand is { } operand ? Value.Word(Operands.ModeOf(operand)) : Value.Unknown
                : Value.Of(about.Block is null || Macros.LinesOf(about.Block).Count == 0);
        }

        // Where a segment is loaded and where it runs are known only to the linker.
        if (name is ".loadof" or ".runof")
        {
            if (arguments.Count != 1)
                Report(function, Catalogue.BuiltinArguments.Message(name, "a segment"));
            return Value.Unknown;
        }

        // `.endof` and `.spanof` describe layout rather than shape. They are address
        // expressions like any label difference. Only the difference can ever be a number,
        // because nt65 never knows an absolute address, and only a caller that has laid out the
        // file can supply it.
        if (name is ".endof" or ".spanof")
        {
            if (arguments.Count != 1 || SymbolOf(arguments[0]) is not { } laid)
                return Value.Unknown;
            if (NotAnExtent(laid, name, arguments[0]))
                return Value.Unknown;
            if (!HasBytesOfItsOwn(laid))
            {
                Report(arguments[0], Catalogue.NothingToMeasure.Message(laid.Name, laid.KindPhrase, name));
                return Value.Unknown;
            }
            return name == ".spanof" && spans?.Invoke(laid) is { } span ? Value.Of(span) : Value.Unknown;
        }

        // `.mincycles` and `.maxcycles` return what one pass over a span of code costs. Both
        // ends are positions in one routine, and only a caller that has laid out the file can
        // count what lies between them.
        if (name is ".mincycles" or ".maxcycles")
        {
            if (arguments.Count != 2)
            {
                Report(function, Catalogue.BuiltinArguments.Message(name, $"`{name}(from, to)`"));
                return Value.Unknown;
            }
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
            if (cycles?.Invoke(start, end, name == ".maxcycles") is not { } counted)
                return Value.Unknown;
            if (counted.Problem is { } problem)
            {
                Report(function, Catalogue.CyclesSpanHasNoBound.Message(name, problem));
                return Value.Unknown;
            }
            return counted.Value is { } number ? Value.Of(number) : Value.Unknown;
        }

        if (name is ".sizeof" or ".countof")
        {
            if (arguments.Count != 1 || SymbolOf(arguments[0]) is not { } measured)
                return Value.Unknown;

            // `.countof(p)` of a `list` parameter is the number of arguments the call gave it.
            if (name == ".countof" && names.Argument(arguments[0]) is { Parameter.Kind: ParameterKind.List } listed)
                return Value.Of(listed.Items.Count);
            if (NotAnExtent(measured, name, arguments[0]))
                return Value.Unknown;

            // An enum counts its members.
            if (name == ".countof" && measured.Kind == SymbolKind.Enum)
                return Value.Of(measured.Body?.Symbols.Count(member => member.Kind == SymbolKind.Constant) ?? 0);

            // A routine and mixed data are measured in bytes, not elements. How many bytes a
            // routine takes is a matter of layout, which only a caller that has laid out the
            // file knows.
            var bytesOnly = measured.Kind == SymbolKind.Proc || measured is { Kind: SymbolKind.Data, Data: null };
            if (name == ".countof" && bytesOnly)
            {
                Report(arguments[0], Catalogue.CountofHasNoElements.Message(
                    measured.Name, (measured.Kind == SymbolKind.Proc ? "a routine" : "mixed data"), measured.Name));
                return Value.Unknown;
            }
            if (measured.Kind == SymbolKind.Proc)
                return spans?.Invoke(measured) is { } body ? Value.Of(body) : Value.Unknown;

            EnsureEvaluated(measured);
            var room = name == ".sizeof" ? measured.Size : measured.Count;
            if (room is null && measured is { Kind: SymbolKind.Data, Data: null })
            {
                Report(arguments[0], Expands(measured)
                    ? Catalogue.SizeofDependsOnExpansion.Message(measured.Name, measured.Name)
                    : Catalogue.SizeofDependsOnAlignment.Message(measured.Name, measured.Name));
            }
            return room is { } number ? Value.Of(number) : Value.Unknown;
        }

        // `.exprof(p)` is the expression inside the operand that the call passed as `p`, such
        // as `5` for `{#5}` or `ptr` for `{(ptr),y}`. It lets a body put an operand's value in
        // data.
        if (name == ".exprof")
        {
            if (names.ExprOf(call) is { } inner)
                return Evaluate(inner);

            // Outside an expansion the parameter has been given no operand yet, which is not an
            // error.
            if (arguments.Count != 1 || names.Parameter(arguments[0]) is not { Parameter.Kind: ParameterKind.Operand })
                Report(function, Catalogue.BuiltinArguments.Message(".exprof", "an `operand` parameter"));
            return Value.Unknown;
        }

        // `.addrsize` asks about the shape of its argument rather than its value.
        if (name == ".addrsize")
        {
            return arguments.Count == 1 && SizeOf(arguments[0], null) is { } size
                ? Value.Of((long)size)
                : Value.Unknown;
        }

        // A condition in an expansion asks about the CPU in the same way the build's
        // conditions do.
        if (configuration is not null
            && Configuration.AboutTheCpu(name, function, arguments, configuration.Cpu,
                (_, message) => Report(function, message)) is { } answer)
        {
            return answer;
        }

        // `.target`, `.has`, `.defined` and the three built-ins only a macro body uses
        // (`.mode`, `.empty`, `.exprof`) are handled before this point, each by the pass that
        // knows what they ask about.
        return Plain(name, function, arguments);
    }

    /// <summary>
    /// Evaluates the built-in functions that are arithmetic on their arguments and ask nothing
    /// about the program. These are the ones a build's conditions may also call.
    /// </summary>
    private Value Plain(string name, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        var values = arguments.Select(Evaluate).ToArray();
        if (name is ".sqrt" or ".muldiv" or ".sin" or ".cos")
            return Worked(name, function, values);
        if (name is ".strsub" or ".strcat")
            return Built(name, function, arguments, values);
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
    private Value Built(string name, SyntaxToken function, IReadOnlyList<SyntaxNode> arguments, Value[] values)
    {
        if (name == ".strsub")
        {
            if (values.Length != 3 || values[0].Kind is ValueKind.Number or ValueKind.Word
                || values[1].Kind is ValueKind.String or ValueKind.Word
                || values[2].Kind is ValueKind.String or ValueKind.Word)
            {
                Report(function, Catalogue.BuiltinArguments.Message(name, "`.strsub(text, start, count)`: a text and two numbers"));
                return Value.Unknown;
            }
            if (values is not [{ Kind: ValueKind.String, Text: { } whole }, { Kind: ValueKind.Number } from,
                { Kind: ValueKind.Number } taken])
            {
                return Value.Unknown;
            }
            if (from.Number < 0 || taken.Number < 0 || from.Number + taken.Number > whole.Length)
            {
                Report(function, Catalogue.StrsubOutOfRange.Message(
                    $"{taken.Number} {(taken.Number == 1 ? "byte" : "bytes")} from {from.Number}",
                    $"{whole.Length} {(whole.Length == 1 ? "byte" : "bytes")} long"));
                return Value.Unknown;
            }
            return Value.Of(whole.Substring((int)from.Number, (int)taken.Number));
        }

        if (values.Length == 0)
        {
            Report(function, Catalogue.BuiltinArguments.Message(name, "`.strcat(part, ...)`: at least one text or number"));
            return Value.Unknown;
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
    private Value Worked(string name, SyntaxToken function, Value[] values)
    {
        if (values.Any(value => value.Kind != ValueKind.Number))
            return Value.Unknown;
        long? worked;
        switch (name)
        {
            case ".sqrt" when values is [var n]:
                worked = IntegerMath.Sqrt(n.Number);
                if (worked is null)
                    Report(function, Catalogue.SqrtOfANegative.Message(n.Number));
                break;
            case ".muldiv" when values is [var a, var b, var c]:
                if (c.Number == 0)
                {
                    Report(function, Catalogue.DivisionByZero);
                    return Value.Unknown;
                }
                worked = IntegerMath.MulDiv(a.Number, b.Number, c.Number);
                if (worked is null)
                    Report(function, Catalogue.ArithmeticOverflow.Message($"`{name}`"));
                break;
            case ".sin" or ".cos" when values is [var angle, var turn, var scale]:
                if (!IntegerMath.InRange(turn.Number, scale.Number))
                {
                    Report(function, Catalogue.TurnOrScaleOutOfRange.Message(name, IntegerMath.Limit));
                    return Value.Unknown;
                }
                worked = name == ".sin"
                    ? IntegerMath.Sin(angle.Number, turn.Number, scale.Number)
                    : IntegerMath.Cos(angle.Number, turn.Number, scale.Number);
                break;
            default:
                Report(function, Catalogue.BuiltinArguments.Message(name, name switch
                {
                    ".sqrt" => "one number",
                    ".muldiv" => "`.muldiv(a, b, c)`",
                    _ => $"`{name}(angle, turn, scale)`",
                }));
                return Value.Unknown;
        }
        return worked is { } number ? Value.Of(number) : Value.Unknown;
    }

    /// <summary>
    /// Returns the value of a name in a build's condition, which can only be a define or a
    /// <c>.config</c> setting. Anything else the program declares is rejected here rather than
    /// looked up, because a check about the program is an <c>.assert</c>, which is evaluated once
    /// the program is known.
    /// </summary>
    private Value InCondition(NameExpressionSyntax name, Conditions asked)
    {
        if (name.SimpleName is { } only && asked.Defines.TryGetValue(only.Text, out var value))
            return Value.Of(value);
        if (asked.Setting(name, Report) is { } setting)
            return setting;
        Report(name, Catalogue.ConditionNamesTheProgram.Message(name.GetText().Trim()));
        return Value.Unknown;
    }

    /// <summary>
    /// Returns the value of a call in a build's condition. Only the built-ins that the
    /// configuration alone can evaluate have a value there. The value of a function the program
    /// declares, and the result of a built-in that measures the program, are not known until
    /// there is a program.
    /// </summary>
    private Value InCondition(CallExpressionSyntax call, IReadOnlyList<SyntaxNode> given, Conditions asked)
    {
        if (call.Function is not { Kind: SyntaxKind.Directive } function)
        {
            // A charmap or a `.func` called by name. Both are declarations, and reaching
            // them means resolving a name before the declarations exist.
            Report(call, Catalogue.ConditionCallsAFunction);
            return Value.Unknown;
        }

        var name = function.Text.ToLowerInvariant();

        // `.defined` asks whether a name is a define, so the name is not looked up at all, and
        // a name that is not a define is the answer rather than a mistake.
        if (name == ".defined")
        {
            return given is [NameExpressionSyntax { SimpleName: { } about }]
                ? Value.Of(asked.Defines.ContainsKey(about.Text))
                : Value.Unknown;
        }

        if (Configuration.AboutTheCpu(name, function, given, asked.Cpu, (_, message) => Report(function, message))
            is { } answer)
        {
            return answer;
        }

        // Only the value the condition chooses is read, so it alone has to be a define.
        if (name == ".select")
        {
            if (given.Count != 3)
            {
                Report(function, Catalogue.SelectArguments);
                return Value.Unknown;
            }
            return Evaluate(given[0]).AsNumber() is { } holds ? Evaluate(given[holds != 0 ? 1 : 2]) : Value.Unknown;
        }

        // What the function asks about is checked before its arguments are read, so a
        // `.sizeof(Point)` is one mistake, not that plus a `Point` that is not a define.
        if (!Answerable(name))
        {
            Report(function, Catalogue.ConditionAsksAboutTheProgram.Message(function.Text));
            return Value.Unknown;
        }
        return Plain(name, function, given);
    }

    /// <summary>
    /// Evaluates <c>.select(c, a, b)</c>, which is <c>a</c> when the constant <c>c</c> holds and
    /// <c>b</c> when it does not. Only the chosen value is evaluated.
    /// </summary>
    private Value Select(SyntaxToken function, IReadOnlyList<SyntaxNode> arguments)
    {
        if (arguments.Count != 3)
        {
            Report(function, Catalogue.SelectArguments);
            return Value.Unknown;
        }
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
    /// Returns the value a <c>.select</c> call chooses, or null when <paramref name="node"/> is
    /// not a <c>.select</c> call or its condition is not a constant.
    /// </summary>
    private SyntaxNode? ChosenBy(SyntaxNode node) =>
        SelectArguments(node) is [var condition, var ifHolds, var otherwise]
            && Evaluate(condition).AsNumber() is { } holds
            ? holds != 0 ? ifHolds : otherwise
            : null;

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
                && mapped.TryGetValue((int)character.Number, out var b)
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
            using (Evaluating(symbol))
                return Evaluate(symbol.Items[0]);
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
    /// Reads a charmap into the mapping it describes. Each entry maps one character to a value,
    /// or a range of characters to consecutive values. A character that no entry names has no
    /// byte, and applying the mapping to it is an error where that happens.
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
}
